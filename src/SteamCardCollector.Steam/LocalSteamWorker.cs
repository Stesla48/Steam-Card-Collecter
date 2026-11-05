using System.Globalization;
using System.Text.Json;
using Steamworks;

namespace SteamCardCollector.Steam;

public sealed record WorkerReply(bool Ready, uint AppId, ulong SteamId, string Message);

public static class LocalSteamWorker
{
    // Run on one dedicated process/thread: Steam's AppID is process-wide.
    public static int Run(string[] args)
    {
        uint appId = 0;
        ulong expectedId = 0;
        var initialized = false;
        try
        {
            if (args.Length != 4 || args[0] != "--local-worker" ||
                !uint.TryParse(args[1], out appId) || appId == 0 ||
                !ulong.TryParse(args[2], out expectedId) || expectedId == 0 ||
                !int.TryParse(args[3], out var expectedPid) || expectedPid <= 0)
                throw new InvalidOperationException("Geçersiz yerel Steam isteği.");

            using var sessionLock = new Mutex(false, @"Local\SteamCardCollector.Playing");
            bool acquired;
            try { acquired = sessionLock.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new InvalidOperationException("Başka bir Steam Card Collector toplama işlemi açık.");
            try
            {
                var expected = new LocalSteamIdentity(expectedId, expectedPid);
                // Do not initialize a game for a different or missing local account.
                if (LocalSteamAccount.Read() != expected)
                    throw new InvalidOperationException("Yerel Steam ve web hesabı eşleşmiyor veya Steam yeniden başlatıldı.");
                SteamNativeRuntime.Validate();
                Environment.SetEnvironmentVariable("SteamAppId", appId.ToString(CultureInfo.InvariantCulture));
                Environment.SetEnvironmentVariable("SteamGameId", appId.ToString(CultureInfo.InvariantCulture));
                if (!SteamAPI.Init())
                    throw new InvalidOperationException("Yerel Steam bağlantısı kurulamadı. Steam çevrimiçi olmalı ve oyun bu hesaba ait olmalı.");
                initialized = true;

                void VerifySession()
                {
                    if (LocalSteamAccount.Read() != expected || !SteamUser.BLoggedOn() ||
                        SteamUser.GetSteamID().m_SteamID != expectedId || SteamUtils.GetAppID().m_AppId != appId)
                        throw new InvalidOperationException("Yerel Steam hesabı değişti veya bağlantı kesildi. Toplama durduruldu.");
                }
                VerifySession();
                if (!SteamApps.BIsSubscribed())
                    throw new InvalidOperationException("Yerel Steam bu oyun için sahiplik doğrulamadı.");

                var lastPing = Environment.TickCount64;
                var stopped = 0;
                _ = Task.Run(() =>
                {
                    try
                    {
                        while (Console.ReadLine() is { } line)
                        {
                            if (line != "ping") break;
                            Interlocked.Exchange(ref lastPing, Environment.TickCount64);
                        }
                    }
                    finally { Volatile.Write(ref stopped, 1); }
                });
                Reply(new(true, appId, expectedId, "Yerel Steam bağlantısı doğrulandı."));
                while (Volatile.Read(ref stopped) == 0 && Environment.TickCount64 - Interlocked.Read(ref lastPing) < 5000)
                {
                    SteamAPI.RunCallbacks();
                    VerifySession();
                    Thread.Sleep(50);
                }
                return 0;
            }
            finally
            {
                if (initialized) { SteamAPI.Shutdown(); initialized = false; }
                sessionLock.ReleaseMutex();
            }
        }
        catch (Exception ex)
        {
            var message = ex is IOException or InvalidOperationException ? ex.Message :
                $"Yerel Steam bileşeni başlatılamadı ({ex.GetType().Name}). Windows paketinin tamamını çıkarın.";
            Reply(new(false, appId, expectedId, message));
            return 1;
        }
        finally { if (initialized) SteamAPI.Shutdown(); }
    }

    private static void Reply(WorkerReply reply)
    {
        Console.WriteLine(JsonSerializer.Serialize(reply));
        Console.Out.Flush();
    }
}
