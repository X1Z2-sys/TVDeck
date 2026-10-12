using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace TVDeck.Core;

public sealed class AdbException(string message) : Exception(message);

public sealed record DeviceRow(string Serial, string State, string Model)
{
    public bool Ready => State == "device";
    public string Title => string.IsNullOrEmpty(Model) ? Serial : Model;
}

/// <summary>Thin async wrapper over adb.exe. All device I/O in the app goes through here.</summary>
public static partial class Adb
{
    [GeneratedRegex(@"^[A-Za-z0-9_]+(\.[A-Za-z0-9_]+)+$")] public static partial Regex PackageName();
    [GeneratedRegex(@"^[\w.\-\[\]:%]+$")] private static partial Regex HostPort();

    static string? _path;
    public static string Path => _path ??= Tools.Find("adb.exe") ?? throw new AdbException(
        "adb was not found. Install Android platform-tools (winget install Google.PlatformTools) or scrcpy.");

    public static async Task<(int Code, byte[] Out, string Err)> RunRawAsync(
        IEnumerable<string> args, string? serial = null, int timeoutSec = 60, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(Path)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        if (serial is not null) { psi.ArgumentList.Add("-s"); psi.ArgumentList.Add(serial); }
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new AdbException("could not start adb");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
        var ms = new MemoryStream();
        var outTask = p.StandardOutput.BaseStream.CopyToAsync(ms, timeout.Token);
        var errTask = p.StandardError.ReadToEndAsync(timeout.Token);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await Task.WhenAll(outTask, errTask, p.WaitForExitAsync(timeout.Token));
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            if (ct.IsCancellationRequested) throw;
            Log.Write("adb", $"timeout {timeoutSec}s: {string.Join(' ', args.Take(4))}");
            throw new AdbException($"adb timed out after {timeoutSec}s ({string.Join(' ', args.Take(3))})");
        }
        var err = await errTask;
        if (p.ExitCode != 0) Log.Write("adb", $"exit {p.ExitCode} in {sw.ElapsedMilliseconds}ms: {string.Join(' ', args.Take(4))} :: {err.Trim()}");
        return (p.ExitCode, ms.ToArray(), err);
    }

    public static async Task<string> RunAsync(IEnumerable<string> args, string? serial = null, int timeoutSec = 60, CancellationToken ct = default)
    {
        var (code, outb, err) = await RunRawAsync(args, serial, timeoutSec, ct);
        var text = (Encoding.UTF8.GetString(outb) + err).Trim();
        if (code != 0) throw new AdbException(text.Length > 0 ? text : $"adb exited with code {code}");
        return text;
    }

    public static Task<string> ShellAsync(string serial, string command, int timeoutSec = 60, CancellationToken ct = default)
        => RunAsync(["shell", command], serial, timeoutSec, ct);

    /// <summary>Single-quote a value for the device's /system/bin/sh.</summary>
    public static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";

    public static void RequirePackage(string pkg)
    {
        if (!PackageName().IsMatch(pkg)) throw new AdbException($"'{pkg}' is not a valid package name");
    }

    public static async Task<List<DeviceRow>> DevicesAsync()
    {
        var text = await RunAsync(["devices", "-l"], timeoutSec: 15);
        var rows = new List<DeviceRow>();
        foreach (var line in text.Split('\n').Skip(1))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var model = parts.Skip(2).FirstOrDefault(p => p.StartsWith("model:"))?[6..].Replace('_', ' ') ?? "";
            rows.Add(new DeviceRow(parts[0], parts[1], model));
        }
        return rows;
    }

    public static async Task<string> ConnectAsync(string host)
    {
        host = host.Trim();
        if (!HostPort().IsMatch(host)) throw new AdbException("Enter an IP address like 192.168.1.50 or 192.168.1.50:5555");
        if (!host.Contains(':')) host += ":5555";
        var outp = await RunAsync(["connect", host], timeoutSec: 15);
        if (!outp.Contains("connected") || outp.Contains("cannot") || outp.Contains("failed")) throw new AdbException(outp);
        return outp;
    }

    public static Task<string> PairAsync(string hostPort, string code)
    {
        if (!HostPort().IsMatch(hostPort.Trim()) || !Regex.IsMatch(code.Trim(), @"^\d{6}$"))
            throw new AdbException("Pairing needs the ip:port and the 6-digit code shown on the TV");
        return RunAsync(["pair", hostPort.Trim(), code.Trim()], timeoutSec: 20);
    }

    /// <summary>Hosts advertised over mDNS (wireless debugging on Android 11+).</summary>
    public static async Task<List<(string Name, string Type, string Address)>> MdnsAsync()
    {
        var text = await RunAsync(["mdns", "services"], timeoutSec: 8);
        return text.Split('\n').Skip(1)
            .Select(l => l.Split((char[])['\t', ' '], StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length >= 3)
            .Select(p => (p[0], p[1], p[^1])).ToList();
    }

    /// <summary>TCP-probe the local /24 subnets for open ADB ports (5555).</summary>
    public static async Task<List<string>> ScanLanAsync(CancellationToken ct = default)
    {
        var prefixes = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && IsPrivate(a))
            .Select(a => string.Join('.', a.ToString().Split('.')[..3]))
            .Distinct().ToList();

        var found = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var gate = new SemaphoreSlim(128);
        var probes = prefixes.SelectMany(pre => Enumerable.Range(1, 254).Select(i => $"{pre}.{i}")).Select(async ip =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var c = new System.Net.Sockets.TcpClient();
                using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
                t.CancelAfter(400);
                await c.ConnectAsync(ip, 5555, t.Token);
                found.Add(ip);
            }
            catch { }
            finally { gate.Release(); }
        });
        await Task.WhenAll(probes);
        return found.OrderBy(ip => Version.Parse(ip)).ToList();
    }

    static bool IsPrivate(System.Net.IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] is >= 16 and <= 31);
    }
}

/// <summary>Locates external tools (adb, scrcpy) on PATH or in the winget scrcpy package.</summary>
public static class Tools
{
    public static string? Find(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = System.IO.Path.Combine(dir.Trim(), exe);
            if (File.Exists(p)) return Resolve(p);
        }
        // scrcpy ships its own adb; also covers machines where only scrcpy is installed
        var scrcpy = ScrcpyDir();
        var bundled = scrcpy is null ? null : System.IO.Path.Combine(scrcpy, exe);
        return bundled is not null && File.Exists(bundled) ? bundled : null;
    }

    static string Resolve(string p)
    {
        try { return new FileInfo(p).ResolveLinkTarget(true)?.FullName ?? p; } catch { return p; }
    }

    /// <summary>Directory containing scrcpy.exe and scrcpy-server (resolves winget's alias symlink).</summary>
    public static string? ScrcpyDir()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = System.IO.Path.Combine(dir.Trim(), "scrcpy.exe");
            if (!File.Exists(p)) continue;
            var d = System.IO.Path.GetDirectoryName(Resolve(p))!;
            if (File.Exists(System.IO.Path.Combine(d, "scrcpy-server"))) return d;
        }
        var winget = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Packages");
        return Directory.Exists(winget)
            ? Directory.EnumerateFiles(winget, "scrcpy-server", SearchOption.AllDirectories)
                .Select(System.IO.Path.GetDirectoryName).OrderDescending().FirstOrDefault()
            : null;
    }

    public static string SafeFileName(string name) =>
        string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();

    public static string DataDir
    {
        get
        {
            var d = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TVDeck");
            Directory.CreateDirectory(d);
            return d;
        }
    }
}
