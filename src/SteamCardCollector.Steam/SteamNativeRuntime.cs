using SteamCardCollector.Core;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Steamworks;

namespace SteamCardCollector.Steam;

public static class SteamNativeRuntime
{
    // SDK 1.57, matching Steamworks.NET 20.2.0 and the reference repository.
    public const string ExpectedSha256 = "A44E5537939AE4EEBC69000589AA9B2437A667813A1657CC779198BAE9B815A9";
    static SteamNativeRuntime()
    {
        NativeLibrary.SetDllImportResolver(typeof(SteamAPI).Assembly, (name, _, _) =>
            name is "steam_api64" or "steam_api64.dll" ? LoadVerified() : 0);
    }

    private static nint LoadVerified()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "lib", "steam_api64.dll");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexString(SHA256.HashData(file)) != ExpectedSha256)
            throw new BadImageFormatException(Strings.InvalidNativeLibrary);
        return NativeLibrary.Load(path);
    }

    public static void Validate()
    {
        if (!Environment.Is64BitProcess) throw new BadImageFormatException(Strings.WindowsX64Required);
        var handle = LoadVerified();
        try
        {
            foreach (var export in new[] { "SteamAPI_Init", "SteamAPI_Shutdown", "SteamAPI_RunCallbacks", "SteamAPI_ISteamUser_GetSteamID" })
                if (!NativeLibrary.TryGetExport(handle, export, out _)) throw new EntryPointNotFoundException(export);
            _ = SteamAPI.IsSteamRunning(); // Test managed resolver without initializing an AppID.
        }
        finally { NativeLibrary.Free(handle); }
    }
}
