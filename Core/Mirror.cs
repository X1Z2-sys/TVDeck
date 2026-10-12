using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace TVDeck.Core;

public sealed record MirrorQuality(string Name, int MaxSize, int Fps, int BitRate);

/// <summary>
/// Runs scrcpy-server on the TV and streams its H.264 video straight into the app (no scrcpy window).
/// Protocol (scrcpy v4.x, tunnel_forward): video socket then control socket; video = 4-byte codec id,
/// then 12-byte headers. Header MSB set = session packet (flags, width, height); otherwise
/// 8-byte pts|flags (bit62 config, bit61 keyframe) + 4-byte size + payload.
/// </summary>
public sealed class Mirror : IAsyncDisposable
{
    public static readonly MirrorQuality[] Qualities =
    [
        new("1080p", 1920, 60, 8_000_000),
        new("Native", 0, 60, 20_000_000),
        new("720p", 1280, 30, 3_000_000),
    ];

    const string DevicePath = "/data/local/tmp/tvdeck-scrcpy-server.jar";
    const uint H264 = 0x68323634;

    public event Action<int, int>? Session;                       // width, height
    public event Action<byte[], bool, bool, long>? Packet;        // data, isConfig, isKey, ptsUs
    public event Action<string?>? Stopped;                        // null = clean stop

    public int Width { get; private set; }
    public int Height { get; private set; }

    readonly string _serial;
    readonly CancellationTokenSource _cts = new();
    Process? _server;
    TcpClient? _video, _control;
    NetworkStream? _controlStream;
    int _port;
    readonly SemaphoreSlim _writeLock = new(1, 1);

    Mirror(string serial) => _serial = serial;

    public static async Task<Mirror> StartAsync(string serial, MirrorQuality q)
    {
        var m = new Mirror(serial);
        try { await m.ConnectAsync(q); }
        catch { await m.DisposeAsync(); throw; }
        _ = Task.Run(m.ReadLoop);
        return m;
    }

    static (string Jar, string Version) ServerFiles()
    {
        var dir = Tools.ScrcpyDir() ?? throw new AdbException("scrcpy isn't installed. Install it with: winget install Genymobile.scrcpy");
        var jar = Path.Combine(dir, "scrcpy-server");
        // The server refuses to run unless the client version matches exactly; winget's folder is named scrcpy-win64-vX.Y.
        var v = Regex.Match(dir, @"v(\d+\.\d+(?:\.\d+)?)").Groups[1].Value;
        if (v.Length == 0)
        {
            var exe = Path.Combine(dir, "scrcpy.exe");
            var outp = Process.Start(new ProcessStartInfo(exe, "--version") { RedirectStandardOutput = true, CreateNoWindow = true, UseShellExecute = false })!;
            v = Regex.Match(outp.StandardOutput.ReadToEnd(), @"scrcpy (\S+)").Groups[1].Value;
            outp.WaitForExit();
        }
        if (v.Length == 0) throw new AdbException("Couldn't tell which scrcpy version is installed");
        return (jar, v);
    }

    async Task ConnectAsync(MirrorQuality q)
    {
        var (jar, version) = ServerFiles();
        await Adb.RunAsync(["push", jar, DevicePath], _serial, 60);

        var scid = Random.Shared.Next(1, int.MaxValue).ToString("x8");
        _port = int.Parse((await Adb.RunAsync(["forward", "tcp:0", $"localabstract:scrcpy_{scid}"], _serial)).Trim());

        string[] opts =
        [
            $"scid={scid}", "log_level=info", "tunnel_forward=true", "audio=false", "control=true",
            "video_codec=h264", $"max_size={q.MaxSize}", $"max_fps={q.Fps}", $"video_bit_rate={q.BitRate}",
            "send_device_meta=false", "stay_awake=true", "cleanup=true", "power_on=true", "clipboard_autosync=false",
            // Baseline-friendly stream with frequent keyframes recovers fast from Wi-Fi hiccups
            "video_codec_options=i-frame-interval=2",
        ];
        var psi = new ProcessStartInfo(Adb.Path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in (string[])["-s", _serial, "shell", $"CLASSPATH={DevicePath}", "app_process", "/", "com.genymobile.scrcpy.Server", version, .. opts])
            psi.ArgumentList.Add(a);
        _server = Process.Start(psi) ?? throw new AdbException("Couldn't start the mirroring server");
        var log = new System.Text.StringBuilder();
        _server.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.AppendLine(e.Data); };
        _server.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.AppendLine(e.Data); };
        _server.BeginOutputReadLine(); _server.BeginErrorReadLine();

        // adb accepts the forward connection before the server listens and then drops it,
        // so retry until the server's dummy byte arrives (that's how scrcpy itself does it).
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            if (_server.HasExited) throw new AdbException("Mirroring server stopped: " + Tail(log));
            try
            {
                var c = new TcpClient { NoDelay = true, ReceiveBufferSize = 1 << 20 };
                await c.ConnectAsync("127.0.0.1", _port);
                var b = new byte[1];
                using var t = new CancellationTokenSource(1500);
                if (await c.GetStream().ReadAsync(b, t.Token) == 1) { _video = c; break; }
                c.Dispose();
            }
            catch (Exception e) when (e is SocketException or IOException or OperationCanceledException) { }
            if (DateTime.UtcNow > deadline) throw new AdbException("The TV didn't start mirroring in time. " + Tail(log));
            await Task.Delay(150);
        }
        _control = new TcpClient { NoDelay = true };
        await _control.ConnectAsync("127.0.0.1", _port);
        _controlStream = _control.GetStream();
        _ = DrainControl();

        var codec = await ReadExact(4);
        if (BinaryPrimitives.ReadUInt32BigEndian(codec) != H264)
            throw new AdbException(BinaryPrimitives.ReadUInt32BigEndian(codec) == 1 ? "The TV's video encoder refused this quality. Try the Light preset." : "The TV couldn't start the video encoder.");
    }

    static string Tail(System.Text.StringBuilder log)
    {
        lock (log) { var s = log.ToString().Trim(); return s.Length > 400 ? s[^400..] : s; }
    }

    async Task<byte[]> ReadExact(int n)
    {
        var buf = new byte[n];
        await _video!.GetStream().ReadExactlyAsync(buf, _cts.Token);
        return buf;
    }

    async Task ReadLoop()
    {
        string? error = null;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var h = await ReadExact(12);
                if ((h[0] & 0x80) != 0)
                {
                    Width = BinaryPrimitives.ReadInt32BigEndian(h.AsSpan(4));
                    Height = BinaryPrimitives.ReadInt32BigEndian(h.AsSpan(8));
                    Session?.Invoke(Width, Height);
                    continue;
                }
                var ptsFlags = BinaryPrimitives.ReadUInt64BigEndian(h);
                var size = BinaryPrimitives.ReadInt32BigEndian(h.AsSpan(8));
                if (size is <= 0 or > 64 << 20) throw new IOException($"Corrupt video packet ({size} bytes)");
                var data = await ReadExact(size);
                var config = (ptsFlags & (1UL << 62)) != 0;
                var key = (ptsFlags & (1UL << 61)) != 0;
                Packet?.Invoke(data, config, key, config ? 0 : (long)(ptsFlags & ((1UL << 61) - 1)));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or SocketException or EndOfStreamException or ObjectDisposedException)
        {
            if (!_cts.IsCancellationRequested) error = "The connection to the TV dropped.";
        }
        Stopped?.Invoke(error);
    }

    // The server sends clipboard/ack messages we don't use; keep reading so its buffer never fills.
    async Task DrainControl()
    {
        var buf = new byte[4096];
        try { while (await _controlStream!.ReadAsync(buf, _cts.Token) > 0) { } } catch { }
    }

    async Task Send(byte[] msg)
    {
        if (_controlStream is null) return;
        await _writeLock.WaitAsync();
        try { await _controlStream.WriteAsync(msg, _cts.Token); }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException) { }
        finally { _writeLock.Release(); }
    }

    // ---- control messages (layouts from scrcpy app/src/control_msg.c) ----

    /// <summary>action: 0 = down, 1 = up.</summary>
    public Task Key(int keycode, int action, int repeat = 0, int meta = 0)
    {
        var b = new byte[14];
        b[0] = 0; b[1] = (byte)action;
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(2), keycode);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(6), repeat);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(10), meta);
        return Send(b);
    }

    public async Task Tap(int keycode)
    {
        await Key(keycode, 0);
        await Key(keycode, 1);
    }

    public Task Text(string text)
    {
        var utf8 = System.Text.Encoding.UTF8.GetBytes(text.Length > 300 ? text[..300] : text);
        var b = new byte[5 + utf8.Length];
        b[0] = 1;
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(1), utf8.Length);
        utf8.CopyTo(b, 5);
        return Send(b);
    }

    /// <summary>Mouse as a touch pointer. action: 0 down, 1 up, 2 move. x/y in video-frame pixels.</summary>
    public Task Touch(int action, int x, int y, bool pressed)
    {
        if (Width == 0) return Task.CompletedTask;
        var b = new byte[32];
        b[0] = 2; b[1] = (byte)action;
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(2), ulong.MaxValue);   // POINTER_ID_MOUSE (-1)
        WritePosition(b.AsSpan(10), x, y);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(22), (ushort)(action == 1 ? 0 : 0xFFFF));
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(24), action == 2 ? 0 : 1);   // action button: primary
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(28), pressed ? 1 : 0);
        return Send(b);
    }

    /// <summary>Scroll in notches; positive v = up.</summary>
    public Task Scroll(int x, int y, float h, float v)
    {
        if (Width == 0) return Task.CompletedTask;
        static short Fp(float f) => (short)Math.Clamp(f / 16f * 0x8000, short.MinValue, short.MaxValue);
        var b = new byte[21];
        b[0] = 3;
        WritePosition(b.AsSpan(1), x, y);
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(13), Fp(h));
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(15), Fp(v));
        return Send(b);
    }

    /// <summary>Right-click: Back, or wake the screen if it's off.</summary>
    public async Task BackOrScreenOn()
    {
        await Send([4, 0]);
        await Send([4, 1]);
    }

    void WritePosition(Span<byte> b, int x, int y)
    {
        BinaryPrimitives.WriteInt32BigEndian(b, x);
        BinaryPrimitives.WriteInt32BigEndian(b[4..], y);
        BinaryPrimitives.WriteUInt16BigEndian(b[8..], (ushort)Width);
        BinaryPrimitives.WriteUInt16BigEndian(b[10..], (ushort)Height);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _video?.Dispose();
        _control?.Dispose();
        try { if (_server is { HasExited: false }) _server.Kill(true); } catch { }
        if (_port != 0) try { await Adb.RunAsync(["forward", "--remove", $"tcp:{_port}"], _serial, 5); } catch (AdbException) { }
    }

    /// <summary>Open the full scrcpy window with settings tuned for TV (audio, 60 fps, small jitter buffer).</summary>
    public static void PopOut(string serial, MirrorQuality q, bool audio)
    {
        var dir = Tools.ScrcpyDir() ?? throw new AdbException("scrcpy isn't installed. Install it with: winget install Genymobile.scrcpy");
        var psi = new ProcessStartInfo(Path.Combine(dir, "scrcpy.exe")) { UseShellExecute = false, CreateNoWindow = true };
        psi.Environment["ADB"] = Adb.Path;
        foreach (var a in new[]
        {
            "-s", serial, "--window-title", "TVDeck — " + serial,
            $"--max-fps={q.Fps}", $"--video-bit-rate={q.BitRate}", $"--max-size={q.MaxSize}",
            "--video-buffer=50",              // 50 ms absorbs Wi-Fi jitter → visibly smoother motion
            "--stay-awake", "--shortcut-mod=lalt,ralt",
            audio ? "--audio-buffer=80" : "--no-audio",
        }) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }
}

/// <summary>Android KeyEvent keycodes used by the remote and keyboard mapping.</summary>
public static class Keys
{
    public static readonly Dictionary<string, int> Code = new()
    {
        ["HOME"] = 3, ["BACK"] = 4, ["DPAD_UP"] = 19, ["DPAD_DOWN"] = 20, ["DPAD_LEFT"] = 21, ["DPAD_RIGHT"] = 22,
        ["DPAD_CENTER"] = 23, ["VOLUME_UP"] = 24, ["VOLUME_DOWN"] = 25, ["POWER"] = 26, ["TAB"] = 61, ["SPACE"] = 62,
        ["ENTER"] = 66, ["DEL"] = 67, ["MENU"] = 82, ["SEARCH"] = 84, ["MEDIA_PLAY_PAUSE"] = 85, ["MEDIA_NEXT"] = 87,
        ["MEDIA_PREVIOUS"] = 88, ["MEDIA_REWIND"] = 89, ["MEDIA_FAST_FORWARD"] = 90, ["PAGE_UP"] = 92, ["PAGE_DOWN"] = 93,
        ["ESCAPE"] = 111, ["FORWARD_DEL"] = 112, ["MOVE_HOME"] = 122, ["MOVE_END"] = 123, ["VOLUME_MUTE"] = 164,
        ["INFO"] = 165, ["CHANNEL_UP"] = 166, ["CHANNEL_DOWN"] = 167, ["GUIDE"] = 172, ["SETTINGS"] = 176,
        ["TV_INPUT"] = 178, ["APP_SWITCH"] = 187, ["SLEEP"] = 223, ["WAKEUP"] = 224, ["CAPTIONS"] = 175,
    };
}
