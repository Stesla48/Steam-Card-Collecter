namespace SteamCardCollector.Core;

public sealed class GameSelectionSession(ISteamSession inner, IReadOnlySet<uint> selected) : ISteamSession
{
    public IReadOnlyList<uint> UnresolvedAppIds => Array.Empty<uint>();

    public async Task<IReadOnlyList<CardGame>> GetCardGamesAsync(CancellationToken cancellationToken)
    {
        var games = await inner.GetCardGamesAsync(cancellationToken);
        return games.Where(game => selected.Contains(game.AppId)).ToArray();
    }

    public Task StartGameAsync(uint appId, CancellationToken cancellationToken) => inner.StartGameAsync(appId, cancellationToken);
    public Task StopGameAsync(uint appId) => inner.StopGameAsync(appId);
    public Task StopAllAsync() => inner.StopAllAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
