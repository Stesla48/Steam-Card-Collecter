using SteamCardCollector.Core;

namespace SteamCardCollector.Steam;

// Account checks are enforced here, not only by disabled UI buttons.
public sealed class LocalFarmingSession(ICardSource cards, ILocalSteamPlayer player) : ISteamSession
{
    private readonly LocalSteamIdentity identity = player.ReadIdentity();
    private HashSet<uint> verifiedCandidates = new();
    public IReadOnlyList<uint> UnresolvedAppIds => cards.UnresolvedAppIds;

    public void EnsureSameAccount()
    {
        var local = player.ReadIdentity();
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

    public async Task SetPlayingAsync(uint? appId, CancellationToken cancellationToken)
    {
        if (appId is null) { await player.StopAsync(cancellationToken); return; }
        EnsureSameAccount();
        if (!verifiedCandidates.Contains(appId.Value))
            throw new InvalidOperationException(Strings.CardEligibilityIsUnverifiedSelectRefreshFirst);
        await player.StartAsync(appId.Value, cards.SteamId, cancellationToken);
        try { EnsureSameAccount(); }
        catch { await player.StopAsync(CancellationToken.None); throw; }
    }

    public ValueTask DisposeAsync() => player.DisposeAsync();
}
