using SteamCardCollector.Core;

namespace SteamCardCollector.Steam;

// Account checks are enforced here, not only by disabled UI buttons.
public sealed class LocalFarmingSession(ICardSource cards, Func<LocalSteamIdentity> readIdentity, Func<ILocalSteamPlayer> createPlayer) : ISteamSession
{
    private readonly LocalSteamIdentity identity = readIdentity();
    private readonly Dictionary<uint, ILocalSteamPlayer> players = new();
    private HashSet<uint> verifiedCandidates = new();
    public IReadOnlyList<uint> UnresolvedAppIds => cards.UnresolvedAppIds;

    public void EnsureSameAccount()
    {
        var local = readIdentity();
        if (cards.SteamId == 0 || local.SteamId != cards.SteamId)
            throw new InvalidOperationException(string.Format(Strings.AccountMismatchDetails, local.SteamId, cards.SteamId));
        if (local != identity)
            throw new InvalidOperationException(Strings.SessionChangedReconnect);
    }

    public async Task<IReadOnlyList<CardGame>> GetCardGamesAsync(CancellationToken cancellationToken)
    {
        EnsureSameAccount();
        verifiedCandidates.Clear();
        var games = await cards.GetCardGamesAsync(cancellationToken);
        EnsureSameAccount();
        verifiedCandidates = games.Where(g => g.RemainingDrops > 0).Select(g => g.AppId).ToHashSet();
        return games;
    }

    public async Task StartGameAsync(uint appId, CancellationToken cancellationToken)
    {
        EnsureSameAccount();
        if (!verifiedCandidates.Contains(appId))
            throw new InvalidOperationException(Strings.CardEligibilityIsUnverifiedSelectRefreshFirst);
        if (players.ContainsKey(appId)) return;
        var player = createPlayer();
        try
        {
            await player.StartAsync(appId, cards.SteamId, cancellationToken);
            EnsureSameAccount();
            players.Add(appId, player);
        }
        catch
        {
            await player.DisposeAsync();
            EnsureSameAccount();
            throw;
        }
    }

    public async Task StopGameAsync(uint appId)
    {
        if (players.Remove(appId, out var player)) await player.DisposeAsync();
    }

    public async Task StopAllAsync()
    {
        var workers = players.Values.ToArray();
        players.Clear();
        await Task.WhenAll(workers.Select(worker => worker.DisposeAsync().AsTask()));
    }

    public ValueTask DisposeAsync() => new(StopAllAsync());
}
