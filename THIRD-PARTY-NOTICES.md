# Third-party components

The card-selection logic, queue and WPF interface are maintained in this repository.
ArchiSteamFarm is not bundled or used as an engine.

| Component | Version | License | Source |
| --- | --- | --- | --- |
| SteamKit2 | 3.4.0 | LGPL-2.1-only | https://github.com/SteamRE/SteamKit/tree/3.4.0 |
| AngleSharp | 1.5.0 | MIT | https://github.com/AngleSharp/AngleSharp/tree/1.5.0 |
| QRCoder | 1.6.0 | MIT | https://github.com/codebude/QRCoder/tree/1.6.0 |
| Steamworks.NET | 20.2.0 | MIT | https://github.com/rlabrecque/Steamworks.NET/releases/tag/20.2.0 |
| protobuf-net / Core | 3.2.56 | Apache-2.0 | https://github.com/protobuf-net/protobuf-net |
| ZstdSharp.Port | 0.8.7 | MIT | https://github.com/oleg-st/ZstdSharp |
| System.IO.Hashing | 10.0.1 | MIT | https://github.com/dotnet/runtime |
| .NET runtime | 8.0.33 | MIT and included notices | https://github.com/dotnet/runtime |

SteamKit2 is an unmodified dynamically linked DLL. The Windows distribution includes
its corresponding upstream source archive and license in `ThirdParty`. Users may replace
the library with a compatible modified build; no integrity check restricts replacement.
Other package license files and runtime notices accompany the distribution.

`lib/steam_api64.dll` is Valve Steamworks SDK 1.57, Windows x64, matching
Steamworks.NET Standalone 20.2.0. It was retrieved from the user's reference
repository https://github.com/Rejixter/SteamAchievementUnlocker/tree/main/lib.
SHA-256: A44E5537939AE4EEBC69000589AA9B2437A667813A1657CC779198BAE9B815A9.
Valve's applicable Steamworks terms govern this binary; the wrapper MIT license
does not relicense Valve's binary. Only this native DLL is integrity checked.

The local-runtime loading pattern was adapted from SteamAchievementUnlocker
(Copyright (c) 2026 Kaan, MIT); its license is included in ThirdParty.
Steam and Steamworks are Valve trademarks. This project is not affiliated with Valve.
