namespace SteamCardCollector.Steam;

public static class SteamImages
{
    public const int MaxBytes = 512 * 1024;
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        CheckCertificateRevocationList = true
    })
    { Timeout = TimeSpan.FromSeconds(20) };

    public static async Task<byte[]?> TryHeaderAsync(uint appId, CancellationToken cancellationToken)
    {
        foreach (var host in new[] { "cdn.akamai.steamstatic.com", "cdn.cloudflare.steamstatic.com" })
        {
            var bytes = await TryDownloadAsync(new Uri($"https://{host}/steam/apps/{appId}/header.jpg"), uri => IsHeader(uri, appId), cancellationToken);
            if (bytes is { Length: > 0 }) return bytes;
        }
        return null;
    }

    public static Task<byte[]?> TryAvatarAsync(Uri uri, CancellationToken cancellationToken) =>
        TryDownloadAsync(uri, SteamWeb.IsSteamImage, cancellationToken);

    private static bool IsHeader(Uri uri, uint appId) =>
        SteamWeb.IsSteamImage(uri) && uri.AbsolutePath.Contains($"/steam/apps/{appId}/", StringComparison.OrdinalIgnoreCase);

    private static async Task<byte[]?> TryDownloadAsync(Uri start, Func<Uri, bool> allowed, CancellationToken cancellationToken)
    {
        var current = start;
        for (var hop = 0; hop < 4; hop++)
        {
            if (!allowed(current)) return null;
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var next = response.Headers.Location;
                if (next is null) return null;
                current = next.IsAbsoluteUri ? next : new Uri(current, next);
                continue;
            }
            if (!response.IsSuccessStatusCode) return null;
            if (!allowed(response.RequestMessage?.RequestUri ?? current)) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await SteamWeb.ReadBoundedBytesAsync(stream, MaxBytes, cancellationToken);
        }
        return null;
    }
}
