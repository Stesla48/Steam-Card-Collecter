namespace SteamCardCollector.Core;

public sealed class GameSelectionSession(ISteamSession inner, IReadOnlySet<uint> selected) : ISteamSession
{
    public IReadOnlyList<uint> UnresolvedAppIds => Array.Empty<uint>();

    public async Task<IReadOnlyList<CardGame>> GetCardGamesAsync(CancellationToken cancellationToken)
    {
        var games = await inner.GetCardGamesAsync(cancellationToken);
        return games.Where(game => selected.Contains(game.AppId)).ToArray();
    }

    public Task SetPlayingAsync(uint? appId, CancellationToken cancellationToken) => inner.SetPlayingAsync(appId, cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
