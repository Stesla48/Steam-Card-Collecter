# Steam Card Collector 0.3.0

A Windows x64 desktop app that reads Steam trading card eligibility and uses your local Steam client to show one eligible game as playing.

**Residual risk.** This is third-party software, not a Valve product. You enter your Steam password into this app. A process running as the same Windows user may be able to read your password while it is in memory.

## Getting started

1. Download the Windows x64 ZIP from the release assets. Extract the entire ZIP and keep the files together.
2. Open Steam on this PC and sign in online. Run Steam and the collector as the same Windows user with the same privileges.
3. Open `SteamCardCollector.App.exe`. Choose English, Turkish, German, Spanish, French or Simplified Chinese and follow the sign-in guide.
4. Sign in with the same Steam account and complete Steam Guard if prompted. The web session reads the library and badge pages. Your password and session tokens stay in memory.
5. After sign-in, follow the collection guide. When the Steam IDs match and eligible games are available, select **Start**. Automatic start is off by default.
6. Use **Activity log** to follow progress, **Refresh** to check card counts and **Stop** to end collection. Sign in again to load newly acquired games.

Open **Settings** from either screen to replay either guide or choose the language for the next launch. Language and guide completion are stored in `%LOCALAPPDATA%\SteamCardCollector\preferences.json`.

## How collection works

SteamKit2 reads your library and badges. Steamworks.NET 20.2.0 and the Steam SDK 1.57 x64 DLL handle local presence in a separate process for each game. The local integration was informed by [SteamAchievementUnlocker](https://github.com/Rejixter/SteamAchievementUnlocker).

The collector reads the active Steam process and account. The worker also verifies the actual Steam ID, App ID, online session and game ownership. Closing or restarting Steam, switching accounts, or losing the local connection stops collection. Stop and shutdown end the worker. It also exits when the parent connection closes or its five-second heartbeat expires.

The queue refreshes every three minutes. Unknown individual card counts stay unresolved while verified games proceed. A failed badge page or ownership check stops collection. Drop timing is controlled by Steam. A user observed a real drop with the 0.2.0 local client flow.

## Build and test

On Windows with the .NET 8 SDK or later:

```powershell
dotnet build SteamCardCollector.sln -c Release
dotnet run --project tests/SteamCardCollector.Checks -c Release
dotnet run --project src/SteamCardCollector.App -c Release
powershell -ExecutionPolicy Bypass -File scripts/Publish-Release.ps1
```

The packaging script builds a self-contained x64 app with .NET 8.0.33, runs the queue, account, parser, WPF, onboarding and native checks, and rejects a mismatched worker request. It writes the Windows ZIP, source ZIP and SHA-256 checksums under `dist/0.3.0`. Native DLL integrity is verified before loading. For bundled licenses and the corresponding SteamKit2 source, see [third-party notices](THIRD-PARTY-NOTICES.md).

## GitHub builds and releases

Pushes to main or master and pull requests produce downloadable Actions artifacts. To test another branch, open **Actions → Build and release Windows ZIP → Run workflow**, select the branch and leave the release tag empty.

To publish, set the app project version, commit it, then publish a release targeting that commit with a matching tag (for example, `v0.3.0`). The workflow attaches the Windows ZIP, source ZIP and checksums to the release using GitHub's temporary token. To rebuild an existing release, supply its tag when running the workflow manually. The workflow checks out that tag and replaces the matching assets.
