using SteamCardCollector.Core;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace SteamCardCollector.Steam;

public sealed class LocalSteamPlayer : ILocalSteamPlayer
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private Process? worker;
    private CancellationTokenSource? monitorCancellation;
    private Task? monitor;
    private string? workDirectory;
    private bool disposed;
    public event Action<string>? Interrupted;

    public LocalSteamIdentity ReadIdentity() => LocalSteamAccount.Read();

    public async Task StartAsync(uint appId, ulong expectedSteamId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await StopCoreAsync();
            var identity = ReadIdentity();
            if (expectedSteamId == 0 || identity.SteamId != expectedSteamId)
                throw new InvalidOperationException(Strings.TheLocalAndWebSteamAccountsDiffer);
            if (appId == 0) throw new InvalidOperationException(Strings.InvalidGameId);

            // A private working directory avoids overwriting a shared steam_appid.txt.
            workDirectory = Path.Combine(Path.GetTempPath(), "SteamCardCollector-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDirectory);
            await File.WriteAllTextAsync(Path.Combine(workDirectory, "steam_appid.txt"), appId.ToString(CultureInfo.InvariantCulture), cancellationToken);
            var info = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException(Strings.CouldNotLocateTheApplicationExecutable))
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workDirectory,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            if (Path.GetFileNameWithoutExtension(info.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                info.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
            info.ArgumentList.Add("--local-worker");
            info.ArgumentList.Add(appId.ToString(CultureInfo.InvariantCulture));
            info.ArgumentList.Add(expectedSteamId.ToString(CultureInfo.InvariantCulture));
            info.ArgumentList.Add(identity.ProcessId.ToString(CultureInfo.InvariantCulture));
            info.Environment["SCC_LANGUAGE"] = CultureInfo.CurrentUICulture.Name;
            info.Environment["SteamAppId"] = appId.ToString(CultureInfo.InvariantCulture);
            info.Environment["SteamGameId"] = appId.ToString(CultureInfo.InvariantCulture);
            worker = Process.Start(info) ?? throw new IOException(Strings.CouldNotStartTheLocalSteamProcess);
            var stderr = DrainAsync(worker.StandardError);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await worker.StandardInput.WriteLineAsync("ping".AsMemory(), timeout.Token);
            var line = await worker.StandardOutput.ReadLineAsync(timeout.Token);
            var reply = ReadReply(line);
            if (reply is { Ready: false, GameUnavailable: true } && reply.AppId == appId && reply.SteamId == expectedSteamId)
                throw new GameUnavailableException(reply.Message);
            if (reply is null || !reply.Ready || reply.AppId != appId || reply.SteamId != expectedSteamId)
                throw new InvalidOperationException(reply?.Message ?? Strings.WorkerHandshakeFailed);
            if (ReadIdentity() != identity) throw new InvalidOperationException(Strings.TheLocalSteamSessionChanged);
            var remainingOutput = ReadTailAsync(worker.StandardOutput, MaxWorkerReplyChars);
            monitorCancellation = new();
            monitor = MonitorAsync(worker, identity, remainingOutput, stderr, monitorCancellation.Token);
        }
        catch { await StopCoreAsync(); throw; }
        finally { gate.Release(); }
    }

    private async Task MonitorAsync(Process process, LocalSteamIdentity identity, Task<string> output, Task stderr, CancellationToken token)
    {
        string? failure = null;
        try
        {
            while (true)
            {
                await Task.Delay(500, token);
                if (process.HasExited)
                {
                    var reply = ReadReply(await output);
                    throw new IOException(reply?.Message ?? Strings.TheLocalSteamProcessClosedCollectionStopped);
                }
                if (ReadIdentity() != identity) throw new IOException(Strings.SessionChangedReconnect);
                await process.StandardInput.WriteLineAsync("ping".AsMemory(), token);
                await process.StandardInput.FlushAsync(token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) failure = ex.Message; }
        finally
        {
            await EndProcessAsync(process);
            await stderr;
        }
        if (failure is not null) Interrupted?.Invoke(failure);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Cleanup must also run when its caller's operation has already been cancelled.
        await gate.WaitAsync(CancellationToken.None);
        try { await StopCoreAsync(); }
        finally { gate.Release(); }
    }

    private async Task StopCoreAsync()
    {
        monitorCancellation?.Cancel();
        if (monitor is not null) await monitor;
        else if (worker is not null) await EndProcessAsync(worker);
        worker?.Dispose(); worker = null;
        monitorCancellation?.Dispose(); monitorCancellation = null; monitor = null;
        if (workDirectory is not null)
        {
            // Only remove the exact file/directory created by this instance; never recurse.
            try { File.Delete(Path.Combine(workDirectory, "steam_appid.txt")); Directory.Delete(workDirectory); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            workDirectory = null;
        }
    }

    private const int MaxWorkerReplyChars = 4096;
    private static readonly JsonSerializerOptions WorkerReplyJson = new() { MaxDepth = 8 };

    private static WorkerReply? ReadReply(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > MaxWorkerReplyChars) return null;
        try { return JsonSerializer.Deserialize<WorkerReply>(line, WorkerReplyJson); }
        catch (JsonException) { return null; }
    }

    private static async Task<string> ReadTailAsync(StreamReader reader, int maxChars)
    {
        var buffer = new char[1024];
        var kept = new StringBuilder();
        while (true)
        {
            int read;
            try { read = await reader.ReadAsync(buffer); }
            catch (IOException) { break; }
            catch (ObjectDisposedException) { break; }
            if (read == 0) break;
            var room = maxChars - kept.Length;
            if (room > 0) kept.Append(buffer, 0, Math.Min(read, room));
        }
        return kept.ToString();
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[1024];
        while (true)
        {
            int read;
            try { read = await reader.ReadAsync(buffer); }
            catch (IOException) { return; }
            catch (ObjectDisposedException) { return; }
            if (read == 0) return;
        }
    }

    private static async Task EndProcessAsync(Process process)
    {
        try { process.StandardInput.Close(); } catch (IOException) { }
        if (!process.HasExited)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { if (disposed) return; disposed = true; await StopCoreAsync(); }
        finally { gate.Release(); }
    }
}
