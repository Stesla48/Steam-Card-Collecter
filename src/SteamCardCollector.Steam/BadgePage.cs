using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using SteamCardCollector.Core;

namespace SteamCardCollector.Steam;

public sealed record BadgePage(IReadOnlyList<CardGame> Games, int LastPage, IReadOnlyList<uint> UnresolvedAppIds);

public static class BadgePageParser
{
    public static BadgePage Parse(string html, ulong expectedSteamId, IReadOnlySet<uint>? ownedAppIds = null)
    {
        // This identity is emitted by Steam for the logged-in viewer, not the profile owner.
        var identity = Regex.Match(html, "g_steamID\\s*=\\s*[\"'](?<id>[0-9]+)[\"']");
        if (!identity.Success || identity.Groups["id"].Value != expectedSteamId.ToString(CultureInfo.InvariantCulture))
            throw new InvalidDataException("Steam web oturumu doğrulanamadı. Yeniden bağlanın.");
        var doc = new HtmlParser().ParseDocument(html);
        if (doc.QuerySelector(".badges_sheet") is null)
            throw new InvalidDataException("Steam rozet sayfası okunamadı; işlem durduruldu.");
        var games = new List<CardGame>();
        var unresolved = new HashSet<uint>();
        foreach (var row in doc.QuerySelectorAll(".badge_row"))
        {
            var href = row.QuerySelector("a.badge_row_overlay")?.GetAttribute("href") ?? "";
            var id = Regex.Match(href, @"/gamecards/(?<id>\d+)(?:/|\?|$)");
            if (!id.Success) continue; // Event badges are not games.
            if (href.Contains("border=1", StringComparison.OrdinalIgnoreCase)) continue; // Foil badge duplicates.
            if (!uint.TryParse(id.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var appId) || appId == 0)
                continue;
            // Badges can remain after a refund or from a game not owned by this account.
            // Steam does not necessarily render drop counters for those rows.
            if (ownedAppIds is not null && !ownedAppIds.Contains(appId)) continue;
            var drops = row.QuerySelector(".badge_title_stats_content .progress_info_bold, .progress_info_bold")?.TextContent;
            if (drops is null) { unresolved.Add(appId); continue; }
            drops = Regex.Replace(drops, @"\s+", " ").Trim().Trim('(', ')').Trim();
            if (drops.Contains("No card drops remaining", StringComparison.OrdinalIgnoreCase)) continue;
            var count = Regex.Match(drops, @"^(?<count>[0-9]+)\s+card drops? remaining\b", RegexOptions.IgnoreCase);
            if (!count.Success) { unresolved.Add(appId); continue; }
            if (!int.TryParse(count.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var remaining))
            { unresolved.Add(appId); continue; }
            if (remaining == 0) continue;
            var name = row.QuerySelector(".badge_title")?.TextContent.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            games.Add(new(appId, name ?? $"Oyun {appId}", remaining));
        }
        var lastPage = 1;
        foreach (var link in doc.QuerySelectorAll(".pagelink"))
        {
            var match = Regex.Match(link.GetAttribute("href") ?? "", @"[?&]p=(\d+)");
            if (match.Success)
            {
                if (!int.TryParse(match.Groups[1].Value, out var page) || page < 1 || page > 1000)
                    throw new InvalidDataException("Beklenmeyen rozet sayfası sayısı.");
                lastPage = Math.Max(lastPage, page);
            }
        }
        if (lastPage > 1000) throw new InvalidDataException("Beklenmeyen rozet sayfası sayısı.");
        return new(games, lastPage, unresolved.ToArray());
    }
}
