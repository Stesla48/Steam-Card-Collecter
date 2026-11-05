namespace SteamCardCollector.Core;

public readonly record struct TrackedDrop(string Name, int Remaining);
public readonly record struct CardDrop(string Name, int Count, int Remaining);

public static class DropEvents
{
    public static IReadOnlyList<CardDrop> Find(IReadOnlyDictionary<uint, TrackedDrop> previous, IReadOnlyList<CardGame> current)
    {
        var drops = new List<CardDrop>();
        if (previous.Count == 0) return drops;
        var now = current.ToDictionary(game => game.AppId);
        foreach (var (appId, prior) in previous)
        {
            if (prior.Remaining <= 0) continue;
            var remaining = now.TryGetValue(appId, out var game) ? game.RemainingDrops : 0;
            if (remaining >= prior.Remaining) continue;
            var name = now.TryGetValue(appId, out var named) && !string.IsNullOrWhiteSpace(named.Name) ? named.Name : prior.Name;
            drops.Add(new(string.IsNullOrWhiteSpace(name) ? $"Oyun {appId}" : name, prior.Remaining - remaining, remaining));
        }
        return drops;
    }

    public static string Format(IReadOnlyList<CardDrop> drops)
    {
        if (drops.Count == 0) return "";
        if (drops.Count == 1)
        {
            var drop = drops[0];
            var tail = drop.Remaining > 0 ? $"{drop.Remaining} hak kaldı." : "Bu oyunun kart hakkı bitti.";
            return drop.Count == 1 ? $"Kart düştü: {drop.Name}. {tail}" : $"{drop.Name} için {drop.Count} kart düştü. {tail}";
        }
        return $"{drops.Sum(drop => drop.Count)} kart düştü: {string.Join(", ", drops.Select(drop => drop.Name))}.";
    }
}
