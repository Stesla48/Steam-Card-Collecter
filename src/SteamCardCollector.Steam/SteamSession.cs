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
    public IReadOnlyList<OwnedGame> Library { get; private set; } = Array.Empty<OwnedGame>();
    public IReadOnlyList<uint> UnresolvedAppIds { get; private set; } = Array.Empty<uint>();
    public bool IsConnected => authenticated && client.IsConnected;
    public event Action<string>? Interrupted;

    public SteamSession()
    {
        user = client.GetHandler<SteamUser>()!;
        callbacks = new(client);
        http = new(new HttpClientHandler { CookieContainer = cookies, AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(45) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SteamCardCollector/0.1");
        subscriptions.Add(callbacks.Subscribe<SteamClient.ConnectedCallback>(_ => connected.TrySetResult()));
        subscriptions.Add(callbacks.Subscribe<SteamUser.LoggedOnCallback>(c => loggedOn.TrySetResult(c)));
        subscriptions.Add(callbacks.Subscribe<SteamClient.DisconnectedCallback>(_ => Fail("Steam bağlantısı kesildi. Yeniden bağlanın.")));
        subscriptions.Add(callbacks.Subscribe<SteamUser.LoggedOffCallback>(_ => Fail("Steam oturumu kapandı. Yeniden bağlanın.")));
    }

    public async Task LoginAsync(string username, string password, IAuthenticator authenticator, CancellationToken cancellationToken)
    {
        if (callbackTask is not null) throw new InvalidOperationException("Bu oturum zaten başlatıldı.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var token = timeout.Token;
        callbackTask = Task.Run(() =>
        {
            try { while (!lifetime.IsCancellationRequested) callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(100)); }
            catch (Exception) { Fail("Steam iletişimi durdu. Yeniden bağlanın."); }
        });
        client.Connect();
        await connected.Task.WaitAsync(token);
        var auth = await client.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
        {
            Username = username, Password = password, IsPersistentSession = false, Authenticator = authenticator
        }).WaitAsync(token);
        var result = await auth.PollingWaitForResultAsync(token);
        refreshToken = result.RefreshToken;
        AccountName = result.AccountName;
        user.LogOn(new SteamUser.LogOnDetails { Username = result.AccountName, AccessToken = result.RefreshToken, ShouldRememberPassword = false });
        var logged = await loggedOn.Task.WaitAsync(token);
        if (logged.Result != EResult.OK) throw new InvalidOperationException($"Steam girişi başarısız: {logged.Result}");
        SteamId = (logged.ClientSteamID ?? throw new IOException("Steam hesap kimliği alınamadı.")).ConvertToUInt64();
        authenticated = true;
        UpdateCookie(result.AccessToken);
        await LoadLibraryAsync(token);
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
                using var response = await http.GetAsync($"https://steamcommunity.com/profiles/{SteamId}/badges/?l=english&p={page}", cancellationToken);
                response.EnsureSuccessStatusCode();
                var uri = response.RequestMessage?.RequestUri;
                if (uri?.Host != "steamcommunity.com" || !uri.AbsolutePath.EndsWith("/badges/", StringComparison.Ordinal))
                    throw new IOException("Steam rozet sayfasına erişilemedi; yeniden bağlanın.");
                var parsed = BadgePageParser.Parse(await response.Content.ReadAsStringAsync(cancellationToken), SteamId, ownedIds);
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

    private void UpdateCookie(string accessToken)
    {
        cookies.Add(new Cookie("steamLoginSecure", Uri.EscapeDataString($"{SteamId}||{accessToken}"), "/", "steamcommunity.com") { Secure = true, HttpOnly = true });
        tokenUpdated = DateTimeOffset.UtcNow;
    }

    private void EnsureConnected()
    {
        if (!IsConnected) throw new IOException("Steam bağlantısı yok. Yeniden bağlanın.");
    }
    private void Fail(string message)
    {
        authenticated = false;
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
            refreshToken = "";
            lifetime.Cancel();
            if (callbackTask is not null) await callbackTask;
            foreach (var subscription in subscriptions) subscription.Dispose();
            http.Dispose();
            lifetime.Dispose();
        }
    }
}
