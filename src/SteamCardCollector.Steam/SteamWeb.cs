using System.Text;
using System.Text.RegularExpressions;

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

    public static bool IsCommunityProfile(Uri? uri)
    {
        if (!IsHttpsHost(uri, "steamcommunity.com")) return false;
        var path = uri!.AbsolutePath;
        return path.Contains("/profiles/", StringComparison.OrdinalIgnoreCase) || path.Contains("/id/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSteamImage(Uri? uri)
    {
        if (!IsHttpsHost(uri, null)) return false;
        var host = uri!.Host;
        var steamStatic = host.Equals("steamstatic.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase);
        var akamai = host.Equals("steamcdn-a.akamaihd.net", StringComparison.OrdinalIgnoreCase);
        if (!steamStatic && !akamai) return false;
        var path = uri.AbsolutePath;
        if (path.Contains("..", StringComparison.Ordinal)) return false;
        return path.Contains("/steam/apps/", StringComparison.OrdinalIgnoreCase) || host.StartsWith("avatars.", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryReadProfile(string xml, ulong expectedSteamId, out string? persona, out Uri? avatar)
    {
        persona = null;
        avatar = null;
        var id = Regex.Match(xml, @"<steamID64>\s*(\d+)\s*</steamID64>", RegexOptions.IgnoreCase);
        if (!id.Success || id.Groups[1].Value != expectedSteamId.ToString()) return false;
        var name = Regex.Match(xml, @"<steamID>\s*<!\[CDATA\[(?<name>[\s\S]*?)\]\]>\s*</steamID>", RegexOptions.IgnoreCase);
        if (name.Success)
        {
            var text = name.Groups["name"].Value.Trim();
            if (text.Length is > 0 and <= 128) persona = text;
        }
        var image = Regex.Match(xml, @"<avatarMedium>\s*<!\[CDATA\[(?<url>https://[^\]\s]+)\]\]>\s*</avatarMedium>", RegexOptions.IgnoreCase);
        if (image.Success && Uri.TryCreate(image.Groups["url"].Value, UriKind.Absolute, out var uri) && IsSteamImage(uri))
            avatar = uri;
        return persona is not null || avatar is not null;
    }

    public static async Task<byte[]?> ReadBoundedBytesAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0) return buffer.ToArray();
            if (buffer.Length > maxBytes - read) return null;
            buffer.Write(chunk, 0, read);
        }
    }

    private static bool IsHttpsHost(Uri? uri, string? host)
    {
        if (uri is null || !uri.IsAbsoluteUri) return false;
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (!uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        return host is null || uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase);
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
