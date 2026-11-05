using System.Net;
using SteamCardCollector.Core;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;

namespace SteamCardCollector.Steam;

public sealed record OwnedGame(uint AppId, string Name);

// Web authentication/library only. Playing presence belongs exclusively to LocalSteamPlayer.
public sealed class SteamSession : ICardSource, IAsyncDisposable
{
    private readonly SteamClient client = new();
    private readonly SteamUser user;
    private readonly CallbackManager callbacks;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<SteamUser.LoggedOnCallback> loggedOn = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<IDisposable> subscriptions = new();
    private readonly SemaphoreSlim webGate = new(1, 1);
    private readonly CookieContainer cookies = new();
    private readonly HttpClient http;
    private Task? callbackTask;
    private volatile bool authenticated;
    private bool disposed;
    private string refreshToken = "";
    private DateTimeOffset tokenUpdated;
    public ulong SteamId { get; private set; }
    public string AccountName { get; private set; } = "";
    public string PersonaName { get; private set; } = "";
    public byte[]? AvatarBytes { get; private set; }
    public IReadOnlyList<OwnedGame> Library { get; private set; } = Array.Empty<OwnedGame>();
    public IReadOnlyList<uint> UnresolvedAppIds { get; private set; } = Array.Empty<uint>();
    public bool IsConnected => authenticated && client.IsConnected;
    public event Action<string>? Interrupted;

    public SteamSession()
    {
        user = client.GetHandler<SteamUser>()!;
        callbacks = new(client);
        http = new(new HttpClientHandler
        {
            CookieContainer = cookies,
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false,
            UseCookies = true,
            CheckCertificateRevocationList = true
        }) { Timeout = TimeSpan.FromSeconds(45) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SteamCardCollector/0.1");
        subscriptions.Add(callbacks.Subscribe<SteamClient.ConnectedCallback>(_ => connected.TrySetResult()));
        subscriptions.Add(callbacks.Subscribe<SteamUser.LoggedOnCallback>(c => loggedOn.TrySetResult(c)));
        subscriptions.Add(callbacks.Subscribe<SteamClient.DisconnectedCallback>(_ => Fail("Steam bağlantısı kesildi. Yeniden bağlanın.")));
        subscriptions.Add(callbacks.Subscribe<SteamUser.LoggedOffCallback>(_ => Fail("Steam oturumu kapandı. Yeniden bağlanın.")));
    }

    public async Task LoginAsync(string username, string? password, IAuthenticator authenticator, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            throw new InvalidOperationException("Steam kullanıcı adı ve şifreni gir.");
        using var timeout = await BeginConnectionAsync(cancellationToken);
        var token = timeout.Token;
        var details = new AuthSessionDetails
        {
            Username = username, Password = password, IsPersistentSession = false, Authenticator = authenticator
        };
        password = null;
        var auth = await client.Authentication.BeginAuthSessionViaCredentialsAsync(details).WaitAsync(token);
        details.Password = "";
        await FinishLoginAsync(await auth.PollingWaitForResultAsync(token), token);
    }

    public async Task LoginWithQrAsync(IProgress<string>? challenge, CancellationToken cancellationToken)
    {
        using var timeout = await BeginConnectionAsync(cancellationToken);
        var token = timeout.Token;
        var details = new AuthSessionDetails
        {
            DeviceFriendlyName = "Steam Card Collector",
            PlatformType = EAuthTokenPlatformType.k_EAuthTokenPlatformType_SteamClient,
            ClientOSType = EOSType.Windows10,
            IsPersistentSession = false,
            WebsiteID = "Client"
        };
        var qr = await client.Authentication.BeginAuthSessionViaQRAsync(details).WaitAsync(token);
        void Publish()
        {
            var url = qr.ChallengeURL;
            if (!string.IsNullOrWhiteSpace(url)) challenge?.Report(url);
        }
        qr.ChallengeURLChanged = Publish;
        try
        {
            Publish();
            await FinishLoginAsync(await qr.PollingWaitForResultAsync(token), token);
        }
        finally { qr.ChallengeURLChanged = null; }
    }

    private async Task<CancellationTokenSource> BeginConnectionAsync(CancellationToken cancellationToken)
    {
        if (callbackTask is not null) throw new InvalidOperationException("Bu oturum zaten başlatıldı.");
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        try
        {
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            callbackTask = Task.Run(() =>
            {
                try { while (!lifetime.IsCancellationRequested) callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(100)); }
                catch (Exception) { Fail("Steam iletişimi durdu. Yeniden bağlanın."); }
            });
            client.Connect();
            await connected.Task.WaitAsync(timeout.Token);
            return timeout;
        }
        catch
        {
            timeout.Dispose();
            throw;
        }
    }

    private async Task FinishLoginAsync(AuthPollResult result, CancellationToken token)
    {
        refreshToken = result.RefreshToken;
        AccountName = result.AccountName;
        user.LogOn(new SteamUser.LogOnDetails { Username = result.AccountName, AccessToken = result.RefreshToken, ShouldRememberPassword = false });
        var logged = await loggedOn.Task.WaitAsync(token);
        if (logged.Result != EResult.OK) throw new InvalidOperationException($"Steam girişi başarısız: {logged.Result}");
        SteamId = (logged.ClientSteamID ?? throw new IOException("Steam hesap kimliği alınamadı.")).ConvertToUInt64();
        authenticated = true;
        UpdateCookie(result.AccessToken);
        await LoadLibraryAsync(token);
        try { await LoadProfileAsync(token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { }
    }

    private async Task LoadLibraryAsync(CancellationToken token)
    {
        EnsureConnected();
        var service = client.GetHandler<SteamUnifiedMessages>()!.CreateService<Player>();
        var response = await service.GetOwnedGames(new CPlayer_GetOwnedGames_Request
        {
            steamid = SteamId, include_appinfo = true, include_played_free_games = true
        }).ToTask().WaitAsync(token);
        if (response.Result != EResult.OK) throw new IOException($"Steam oyun listesi okunamadı: {response.Result}");
        Library = response.Body.games.Select(g => new OwnedGame(checked((uint)g.appid), string.IsNullOrWhiteSpace(g.name) ? $"Oyun {g.appid}" : g.name))
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        EnsureConnected();
    }

    public async Task<IReadOnlyList<CardGame>> GetCardGamesAsync(CancellationToken cancellationToken)
    {
        await webGate.WaitAsync(cancellationToken);
        try
        {
            EnsureConnected();
            if (DateTimeOffset.UtcNow - tokenUpdated > TimeSpan.FromMinutes(10))
            {
                var result = await client.Authentication.GenerateAccessTokenForAppAsync(new SteamID(SteamId), refreshToken).WaitAsync(cancellationToken);
                UpdateCookie(result.AccessToken);
            }
            var games = new Dictionary<uint, CardGame>();
            var unresolved = new HashSet<uint>();
            var ownedIds = Library.Select(g => g.AppId).ToHashSet();
            var last = 1;
            for (var page = 1; page <= last; page++)
            {
                var parsed = BadgePageParser.Parse(await GetBadgeHtmlAsync(page, cancellationToken), SteamId, ownedIds);
                last = Math.Max(last, parsed.LastPage);
                foreach (var game in parsed.Games) games[game.AppId] = game;
                unresolved.UnionWith(parsed.UnresolvedAppIds);
                if (page < last) await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
            EnsureConnected();
            var owned = Library.ToDictionary(g => g.AppId);
            unresolved.ExceptWith(games.Keys);
            UnresolvedAppIds = unresolved.OrderBy(id => id).ToArray();
            return games.Values.Where(g => owned.ContainsKey(g.AppId)).Select(g => g with { Name = owned[g.AppId].Name })
                .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        finally { webGate.Release(); }
    }

    private async Task LoadProfileAsync(CancellationToken cancellationToken)
    {
        var xml = await GetTextAsync(new Uri($"https://steamcommunity.com/profiles/{SteamId}/?xml=1"), SteamWeb.IsCommunityProfile, 200_000, cancellationToken);
        if (!SteamWeb.TryReadProfile(xml, SteamId, out var persona, out var avatar)) return;
        if (!string.IsNullOrWhiteSpace(persona)) PersonaName = persona;
        if (avatar is not null)
            AvatarBytes = await SteamImages.TryAvatarAsync(avatar, cancellationToken);
    }

    private async Task<string> GetTextAsync(Uri start, Func<Uri, bool> allowed, int maxChars, CancellationToken cancellationToken)
    {
        var current = start;
        for (var hop = 0; hop < 4; hop++)
        {
            if (!allowed(current)) throw new IOException("Steam profiline erişilemedi.");
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var next = response.Headers.Location ?? throw new IOException("Steam profiline erişilemedi.");
                current = next.IsAbsoluteUri ? next : new Uri(current, next);
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (!allowed(response.RequestMessage?.RequestUri ?? current))
                throw new IOException("Steam profiline erişilemedi.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await SteamWeb.ReadBoundedTextAsync(stream, maxChars, cancellationToken);
        }
        throw new IOException("Steam profiline erişilemedi.");
    }

    private async Task<string> GetBadgeHtmlAsync(int page, CancellationToken cancellationToken)
    {
        var current = new Uri($"https://steamcommunity.com/profiles/{SteamId}/badges/?l=english&p={page}");
        for (var hop = 0; hop < 5; hop++)
        {
            if (!SteamWeb.IsBadgePage(current))
                throw new IOException("Steam rozet sayfasına erişilemedi; yeniden bağlanın.");
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var next = response.Headers.Location ?? throw new IOException("Steam rozet sayfasına erişilemedi; yeniden bağlanın.");
                current = next.IsAbsoluteUri ? next : new Uri(current, next);
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (!SteamWeb.IsBadgePage(response.RequestMessage?.RequestUri ?? current))
                throw new IOException("Steam rozet sayfasına erişilemedi; yeniden bağlanın.");
            var length = response.Content.Headers.ContentLength;
            if (length is > SteamWeb.MaxBadgePageChars)
                throw new InvalidDataException("Steam rozet sayfası beklenenden büyük.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await SteamWeb.ReadBoundedTextAsync(stream, SteamWeb.MaxBadgePageChars, cancellationToken);
        }
        throw new IOException("Steam rozet sayfasına erişilemedi; yeniden bağlanın.");
    }

    private void UpdateCookie(string accessToken)
    {
        cookies.Add(new Cookie("steamLoginSecure", Uri.EscapeDataString($"{SteamId}||{accessToken}"), "/", "steamcommunity.com") { Secure = true, HttpOnly = true });
        tokenUpdated = DateTimeOffset.UtcNow;
    }

    private void EnsureConnected()
    {
        if (!IsConnected) throw new IOException("Steam bağlantısı yok. Yeniden bağlanın.");
    }
    private void ClearSecrets()
    {
        refreshToken = "";
        PersonaName = "";
        AvatarBytes = null;
        try
        {
            foreach (Cookie cookie in cookies.GetCookies(new Uri("https://steamcommunity.com/")))
            {
                cookie.Value = "";
                cookie.Expired = true;
            }
        }
        catch (Exception) { }
    }
    private void Fail(string message)
    {
        authenticated = false;
        ClearSecrets();
        connected.TrySetException(new IOException(message));
        loggedOn.TrySetException(new IOException(message));
        if (!disposed) Interrupted?.Invoke(message);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            if (client.IsConnected) user.LogOff();
        }
        finally
        {
            client.Disconnect();
            authenticated = false;
            ClearSecrets();
            lifetime.Cancel();
            if (callbackTask is not null) await callbackTask;
            foreach (var subscription in subscriptions) subscription.Dispose();
            http.Dispose();
            lifetime.Dispose();
        }
    }
}
