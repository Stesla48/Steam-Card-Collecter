using SteamCardCollector.Core;
using SteamCardCollector.Steam;

var checkCount = 0;
void Check(bool condition, string message) { checkCount++; if (!condition) throw new Exception(message); }
var session = new TestSession();
await new CardFarmer(session, TimeSpan.FromMilliseconds(1)).RunAsync(null, CancellationToken.None);
Check(session.Played.SequenceEqual(new uint?[] { 10, 20, null }), "Queue must skip zero drops, keep current game, advance, and stop.");
var failure = new TestSession { Fail = true };
try { await new CardFarmer(failure).RunAsync(null, default); throw new Exception("Failure was hidden."); }
catch (IOException) { Check(failure.Played.SequenceEqual(new uint?[] { null }), "Failure must clear playing state."); }
var cancelled = new TestSession();
using var cancellation = new CancellationTokenSource();
cancellation.Cancel();
try { await new CardFarmer(cancelled).RunAsync(null, cancellation.Token); }
catch (OperationCanceledException) { }
Check(cancelled.Played.SequenceEqual(new uint?[] { null }), "Cancellation must clear playing state.");
Console.WriteLine("PASS: queue, filtering, progression, completion, error propagation, cancellation cleanup.");

const ulong steamId = 76561198000000000;
string Page(string rows, string extra = "") => $"<script>var g_steamID = \"{steamId}\";</script><div class='badges_sheet'>{rows}</div>{extra}";
string Row(uint id, string drops, string suffix = "") => $"<div class='badge_row'><a class='badge_row_overlay' href='https://steamcommunity.com/profiles/{steamId}/gamecards/{id}/{suffix}'></a><div class='badge_title'>A &amp; B</div><div class='progress_info_bold'>{drops}</div></div>";
var parsed = BadgePageParser.Parse(Page(Row(10, "3 card drops remaining") + Row(20, "No card drops remaining") + Row(30, "1 card drop remaining") + Row(10, "2 card drops remaining", "?border=1"), "<a class='pagelink' href='?p=3'>3</a>"), steamId);
Check(parsed.Games.Count == 2 && parsed.Games.Sum(g => g.RemainingDrops) == 4 && parsed.Games[0].Name == "A & B" && parsed.LastPage == 3, "Badge parser must read counts, singular/plural, HTML entities, foil duplicates and pages.");
Check(BadgePageParser.Parse(Page(""), steamId).Games.Count == 0, "A verified empty badge page is valid.");
Check(BadgePageParser.Parse(Page(Row(10, " ( 3&nbsp;card drops remaining ) ")), steamId).Games.Single().RemainingDrops == 3,
    "Steam's parenthesized drop text and non-breaking spaces must parse.");
foreach (var badHtml in new[] { "<html>Sign in</html>", Page(Row(10, "3 card drops remaining")).Replace(steamId.ToString(), "76561198000000001") })
{
    try { BadgePageParser.Parse(badHtml, steamId); throw new Exception("Invalid badge data was accepted."); }
    catch (InvalidDataException) { }
}
Console.WriteLine("PASS: authenticated badge parsing, filtering, pagination, singular/plural, foil exclusion, malformed response rejection.");
var missingCounter = "<div class='badge_row'><a class='badge_row_overlay' href='/gamecards/1245620/'></a><div class='badge_title_stats_drops'></div></div>";
var mixed = BadgePageParser.Parse(Page(missingCounter + Row(10, "3 card drops remaining") + Row(30, "Unexpected text")), steamId);
Check(mixed.Games.Count == 1 && mixed.UnresolvedAppIds.SequenceEqual(new uint[] {1245620, 30}), "A missing or unknown counter must not abort other games or become zero drops.");
var filtered = BadgePageParser.Parse(Page(missingCounter + Row(10, "3 card drops remaining")), steamId, new HashSet<uint> {10});
Check(filtered.Games.Count == 1 && filtered.UnresolvedAppIds.Count == 0, "Unowned badge rows must be excluded before reading drop counters.");
var incompleteSession = new IncompleteSession();
try { await new CardFarmer(incompleteSession).RunAsync(null, default); throw new Exception("An incomplete scan was reported as complete."); }
catch (InvalidDataException) { Check(incompleteSession.Cleared, "An incomplete empty scan must clear playing state."); }
Console.WriteLine("PASS: missing counters do not block valid games; unowned rows skipped; incomplete scans never reported as complete.");

var active = new ActiveSession();
var activeFarmer = new CardFarmer(active, TimeSpan.FromHours(1));
using var activeCancellation = new CancellationTokenSource();
var activeTask = activeFarmer.RunAsync(null, activeCancellation.Token);
await active.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
try { await activeFarmer.RunAsync(null, default); throw new Exception("A second farming loop was allowed."); }
catch (InvalidOperationException) { }
activeCancellation.Cancel();
try { await activeTask.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Cancellation was not observed."); }
catch (OperationCanceledException) { }
Check(active.Played.SequenceEqual(new uint?[] { 10, null }), "Active cancellation must clear playing once; rejected duplicate must not stop the first run.");
Console.WriteLine("PASS: duplicate start protection and prompt cancellation during active farming.");

async Task Reject(Func<Task> action, string message)
{
    try { await action(); throw new Exception(message); }
    catch (InvalidOperationException) { Check(true, message); }
}
var web = new FakeCards { SteamId = steamId };
var local = new FakeLocalPlayer { Identity = new(steamId + 1, 100) };
var mismatch = new LocalFarmingSession(web, local);
await Reject(() => mismatch.GetCardGamesAsync(default), "Different accounts must block card farming before any read.");
await Reject(() => mismatch.SetPlayingAsync(10, default), "Different accounts must never launch a game worker.");
Check(local.Started.Count == 0 && web.Reads == 0, "Mismatched accounts reached a side effect.");

local.Identity = new(steamId, 100);
var matched = new LocalFarmingSession(web, local);
await Reject(() => matched.SetPlayingAsync(10, default), "A game must have a verified card counter before launch.");
await matched.GetCardGamesAsync(default);
await Reject(() => matched.SetPlayingAsync(20, default), "An unverified AppID must not launch.");
await matched.SetPlayingAsync(10, default);
Check(local.Started.SequenceEqual(new uint[] { 10 }) && local.ExpectedAccount == steamId, "Matched local play must receive the verified web SteamID.");

local.Identity = new(steamId, 101);
await Reject(() => matched.SetPlayingAsync(10, default), "A restarted client must invalidate the captured session even for the same account.");
await matched.SetPlayingAsync(null, new CancellationToken(true));
Check(local.Stops == 1, "Cleanup must work after the identity changes and with a cancelled token.");

local.Identity = new(steamId + 1, 100);
await Reject(() => matched.SetPlayingAsync(10, default), "A changed local account must invalidate previously verified candidates.");
local.Identity = new(steamId, 100);
web.SteamId = 0;
await Reject(() => matched.GetCardGamesAsync(default), "A missing web account must block farming.");
web.SteamId = steamId;
local.Offline = true;
await Reject(() => matched.GetCardGamesAsync(default), "An unavailable local client must block farming.");
local.Offline = false;

web.OnRead = () => local.Identity = new(steamId + 1, 100);
await Reject(() => matched.GetCardGamesAsync(default), "An account switch during a card HTTP request must reject its result.");
local.Identity = new(steamId, 100); web.OnRead = null;
await Reject(() => matched.SetPlayingAsync(10, default), "A rejected card scan must invalidate earlier candidates.");
await matched.GetCardGamesAsync(default);
local.OnStart = () => local.Identity = new(steamId + 1, 100);
await Reject(() => matched.SetPlayingAsync(10, default), "An account switch during worker startup must be detected and cleaned up.");
Check(local.Stops == 2, "Worker startup identity changes must stop the launched worker.");
Console.WriteLine("PASS: local/web identity mismatch, missing login, client restart, scan/start races, verified candidates and unconditional cleanup.");

var partialWeb = new FakeCards { SteamId = steamId, UnresolvedAppIds = new uint[] { 99 } };
var partialLocal = new FakeLocalPlayer { Identity = new(steamId, 200) };
partialWeb.OnRead = () => { if (partialWeb.Reads > 1) partialWeb.Games = Array.Empty<CardGame>(); };
try
{
    await new CardFarmer(new LocalFarmingSession(partialWeb, partialLocal), TimeSpan.FromMilliseconds(1)).RunAsync(null, default);
    throw new Exception("A partial scan reported completion.");
}
catch (InvalidDataException) { Check(partialLocal.Started.SequenceEqual(new uint[] { 10 }) && partialLocal.Stops == 1, "Unknown counters must not block a valid game; an incomplete tail must stop without claiming completion."); }

var changedWeb = new FakeCards { SteamId = steamId };
var changedLocal = new FakeLocalPlayer { Identity = new(steamId, 300) };
changedWeb.OnRead = () => { if (changedWeb.Reads > 1) changedLocal.Identity = new(steamId + 1, 300); };
await Reject(() => new CardFarmer(new LocalFarmingSession(changedWeb, changedLocal), TimeSpan.FromMilliseconds(1)).RunAsync(null, default), "An active account change must stop farming.");
Check(changedLocal.Started.Count == 1 && changedLocal.Stops == 1, "An account switch must clear local play and never launch the next game.");

var invalidCount = BadgePageParser.Parse(Page(Row(10, "99999999999999999999999 card drops remaining") + Row(20, "2 card drops remaining")), steamId);
Check(invalidCount.Games.Single().AppId == 20 && invalidCount.UnresolvedAppIds.SequenceEqual(new uint[] { 10 }), "An overflowing counter must not block valid games or be treated as zero.");
Console.WriteLine("PASS: valid local queue survives missing/overflowed counters; account change stops active farming.");
Console.WriteLine($"TOTAL: {checkCount} assertions passed. No real Steam login or game initialization.");

sealed class FakeCards : ICardSource
{
 public ulong SteamId { get; set; }
 public IReadOnlyList<uint> UnresolvedAppIds { get; set; } = Array.Empty<uint>();
 public IReadOnlyList<CardGame> Games { get; set; } = new[] { new CardGame(10, "Verified", 1) };
 public Action? OnRead { get; set; }
 public int Reads { get; private set; }
 public Task<IReadOnlyList<CardGame>> GetCardGamesAsync(CancellationToken cancellationToken) { Reads++; OnRead?.Invoke(); return Task.FromResult(Games); }
}

sealed class FakeLocalPlayer : ILocalSteamPlayer
{
 public LocalSteamIdentity Identity { get; set; } = new(0, 0);
 public bool Offline { get; set; }
 public List<uint> Started { get; } = new();
 public ulong ExpectedAccount { get; private set; }
 public int Stops { get; private set; }
 public Action? OnStart { get; set; }
 public LocalSteamIdentity ReadIdentity() => Offline ? throw new InvalidOperationException("Steam offline") : Identity;
 public Task StartAsync(uint appId, ulong expectedSteamId, CancellationToken cancellationToken) { ExpectedAccount = expectedSteamId; Started.Add(appId); OnStart?.Invoke(); return Task.CompletedTask; }
 public Task StopAsync(CancellationToken cancellationToken) { Stops++; return Task.CompletedTask; }
 public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class ActiveSession : ISteamSession
{
 public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
 public List<uint?> Played { get; } = new();
 public Task<IReadOnlyList<CardGame>> GetCardGamesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CardGame>>(new[] { new CardGame(10, "Active", 1) });
 public Task SetPlayingAsync(uint? appId, CancellationToken cancellationToken) { Played.Add(appId); if (appId is not null) Started.TrySetResult(); return Task.CompletedTask; }
 public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class IncompleteSession : ISteamSession
{
 public IReadOnlyList<uint> UnresolvedAppIds => new uint[] {1245620};
 public bool Cleared { get; private set; }
 public Task<IReadOnlyList<CardGame>> GetCardGamesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CardGame>>(Array.Empty<CardGame>());
 public Task SetPlayingAsync(uint? appId, CancellationToken cancellationToken) { Cleared = appId is null; return Task.CompletedTask; }
 public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class TestSession : ISteamSession
{
 public List<uint?> Played { get; } = new();
 public bool Fail { get; init; }
 private int reads;
 public Task<IReadOnlyList<CardGame>> GetCardGamesAsync(CancellationToken cancellationToken)
 {
  if (Fail) throw new IOException("Offline");
  IReadOnlyList<CardGame> games = reads++ switch {
   0 => new[] { new CardGame(1,"No drops",0), new CardGame(10,"First",2), new CardGame(20,"Second",1) },
   1 => new[] { new CardGame(10,"First",1), new CardGame(20,"Second",1) },
   2 => new[] { new CardGame(20,"Second",1) },
   _ => Array.Empty<CardGame>() };
  return Task.FromResult(games);
 }
 public Task SetPlayingAsync(uint? appId, CancellationToken cancellationToken) { Played.Add(appId); return Task.CompletedTask; }
 public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
