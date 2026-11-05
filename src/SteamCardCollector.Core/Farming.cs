namespace SteamCardCollector.Core;

public sealed record CardGame(uint AppId, string Name, int RemainingDrops);
public sealed record FarmingStatus(string Message, CardGame? CurrentGame, int RemainingDrops, IReadOnlyList<CardGame>? Games = null);

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
    Task SetPlayingAsync(uint? appId, CancellationToken cancellationToken);
}

public sealed class CardFarmer(ISteamSession session, TimeSpan? pollingInterval = null)
{
    private readonly TimeSpan interval = pollingInterval ?? TimeSpan.FromMinutes(3);
    private int running;

    public async Task RunAsync(IProgress<FarmingStatus>? progress, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref running, 1) != 0)
            throw new InvalidOperationException(Strings.CollectionIsAlreadyRunning);
        uint? current = null;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var games = (await session.GetCardGamesAsync(cancellationToken))
                    .Where(game => game.RemainingDrops > 0).OrderBy(game => game.AppId).ToArray();
                var next = games.FirstOrDefault(game => game.AppId == current) ?? games.FirstOrDefault();
                if (next is null)
                {
                    if (session.UnresolvedAppIds.Count > 0)
                        throw new InvalidDataException(string.Format(Strings.UnresolvedQueueSummary, session.UnresolvedAppIds.Count));
                    progress?.Report(new(Strings.NoGamesHaveCardDropsRemaining, null, 0, games));
                    return;
                }
                if (current != next.AppId)
                {
                    await session.SetPlayingAsync(next.AppId, cancellationToken);
                    current = next.AppId;
                }
                progress?.Report(new(Strings.WaitingForACardDrop, next, games.Sum(game => game.RemainingDrops), games));
                await Task.Delay(interval, cancellationToken);
            }
        }
        finally
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await session.SetPlayingAsync(null, timeout.Token);
            }
            finally { Volatile.Write(ref running, 0); }
        }
    }
}
