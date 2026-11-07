namespace SteamCardCollector.Core;

public sealed record CardGame(uint AppId, string Name, int RemainingDrops);
public sealed record FarmingStatus(string Message, IReadOnlySet<uint> ActiveIds, int RemainingDrops, IReadOnlyList<CardGame>? Games = null);

public sealed class GameUnavailableException(string message) : InvalidOperationException(message);

public interface ICardSource
{
    ulong SteamId { get; }
    IReadOnlyList<uint> UnresolvedAppIds { get; }
    Task<IReadOnlyList<CardGame>> GetCardGamesAsync(CancellationToken cancellationToken);
}

// Return verified candidates; disclose incomplete row scans through UnresolvedAppIds.
// Authentication and whole-page failures must throw.
public interface ISteamSession : IAsyncDisposable
{
    IReadOnlyList<uint> UnresolvedAppIds => Array.Empty<uint>();
    Task<IReadOnlyList<CardGame>> GetCardGamesAsync(CancellationToken cancellationToken);
    Task StartGameAsync(uint appId, CancellationToken cancellationToken);
    Task StopGameAsync(uint appId);
    Task StopAllAsync();
}

public sealed class CardFarmer(ISteamSession session, TimeSpan? pollingInterval = null)
{
    public const int MaxConcurrentGames = 32;
    private readonly TimeSpan interval = pollingInterval ?? TimeSpan.FromMinutes(3);
    private int running;

    public async Task RunAsync(IProgress<FarmingStatus>? progress, CancellationToken cancellationToken, IReadOnlySet<uint>? selection = null)
    {
        if (Interlocked.Exchange(ref running, 1) != 0)
            throw new InvalidOperationException(Strings.CollectionIsAlreadyRunning);
        var active = new HashSet<uint>();
        var unavailable = new Dictionary<uint, string>();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var games = (await session.GetCardGamesAsync(cancellationToken))
                    .Where(game => game.RemainingDrops > 0).OrderBy(game => game.AppId).ToArray();
                var eligible = games.Where(game => selection == null || selection.Contains(game.AppId)).ToArray();
                var eligibleIds = eligible.Select(game => game.AppId).ToHashSet();
                foreach (var id in active.Except(eligibleIds).ToArray())
                {
                    await session.StopGameAsync(id);
                    active.Remove(id);
                }
                if (eligible.Length == 0)
                {
                    var unresolved = session.UnresolvedAppIds.Count(id => selection == null || selection.Contains(id));
                    if (unresolved > 0)
                        throw new InvalidDataException(string.Format(Strings.UnresolvedQueueSummary, unresolved));
                    progress?.Report(new(Strings.NoGamesHaveCardDropsRemaining, new HashSet<uint>(), 0, games));
                    return;
                }
                foreach (var game in eligible)
                {
                    if (active.Count >= MaxConcurrentGames) break;
                    if (active.Contains(game.AppId) || unavailable.ContainsKey(game.AppId)) continue;
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        await session.StartGameAsync(game.AppId, cancellationToken);
                        active.Add(game.AppId);
                    }
                    catch (GameUnavailableException ex)
                    {
                        unavailable[game.AppId] = $"{game.Name} · {ex.Message}";
                        progress?.Report(new(unavailable[game.AppId], active.ToHashSet(), 0));
                    }
                }
                if (active.Count == 0) throw new InvalidOperationException(unavailable[eligible[0].AppId]);
                progress?.Report(new($"{Strings.Playing}: {active.Count} · {Strings.WaitingForACardDrop}", active.ToHashSet(), eligible.Sum(game => game.RemainingDrops), games));
                await Task.Delay(interval, cancellationToken);
            }
        }
        finally
        {
            try
            {
                await session.StopAllAsync();
            }
            finally { Volatile.Write(ref running, 0); }
        }
    }
}
