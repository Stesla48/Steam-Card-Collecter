using SteamCardCollector.Core;
using System.Diagnostics;
using Microsoft.Win32;

namespace SteamCardCollector.Steam;

public sealed record LocalSteamIdentity(ulong SteamId, int ProcessId);

public static class LocalSteamAccount
{
    // ActiveProcess is the live client, unlike loginusers.vdf's last-used account.
    public static LocalSteamIdentity Read()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(Strings.WindowsIsRequired);
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
        if (key?.GetValue("ActiveUser") is not int account || key.GetValue("pid") is not int pid || account == 0 || pid <= 0)
            throw new InvalidOperationException(Strings.LocalSignInRequired);
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited || !process.ProcessName.Equals("steam", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(Strings.TheLocalSteamClientIsOffline);
        }
        catch (ArgumentException) { throw new InvalidOperationException(Strings.LocalClientClosed); }
        return new(76561197960265728UL + unchecked((uint)account), pid);
    }
}

public interface ILocalSteamPlayer : IAsyncDisposable
{
    LocalSteamIdentity ReadIdentity();
    Task StartAsync(uint appId, ulong expectedSteamId, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}
