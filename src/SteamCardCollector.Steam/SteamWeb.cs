using System.Text;

namespace SteamCardCollector.Steam;

public static class SteamWeb
{
    public const int MaxBadgePageChars = 2_000_000;

    public static bool IsBadgePage(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri) return false;
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (!uri.Host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase)) return false;
        if (!uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        var path = uri.AbsolutePath;
        return path.EndsWith("/badges", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/badges/", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<string> ReadBoundedTextAsync(Stream stream, int maxChars, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 8192, leaveOpen: true);
        var builder = new StringBuilder();
        var buffer = new char[8192];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) return builder.ToString();
            if (read > maxChars - builder.Length)
                throw new InvalidDataException("Steam rozet sayfası beklenenden büyük.");
            builder.Append(buffer, 0, read);
        }
    }
}
