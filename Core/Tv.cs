using System.IO.Compression;
using System.Text.RegularExpressions;

namespace TVDeck.Core;

public sealed record AppRow(string Package, string Installer, bool Disabled, bool System)
{
    static readonly Dictionary<string, string> Stores = new()
    {
        ["com.android.vending"] = "Play Store",
        ["com.amazon.venezia"] = "Amazon Appstore",
    };
    public string Source => Stores.GetValueOrDefault(Installer) ?? (System ? "System" : "Sideloaded");
    public string StatusText => Disabled ? "Disabled" : "";
    public bool FromStore => Stores.ContainsKey(Installer);
}

public sealed record FileRow(string Name, bool IsDir)
{
    public string Glyph => IsDir ? "\uE8B7" : "\uE8A5";
}

/// <summary>Device operations. Every method takes the serial explicitly so callers can't race a device switch.</summary>
public static partial class Tv
{
    public static async Task<Dictionary<string, string>> PropsAsync(string s)
    {
        var text = await Adb.ShellAsync(s, "getprop");
        return PropLine().Matches(text).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
    }

    [GeneratedRegex(@"\[([^\]]+)\]: \[([^\]]*)\]")] private static partial Regex PropLine();

    public static async Task<string> SafeShell(string s, string cmd)
    {
        try { return await Adb.ShellAsync(s, cmd, 15); } catch (AdbException e) { return e.Message; }
    }

    /// <summary>Every fact worth showing about the TV, in display order.</summary>
    public static async Task<List<(string Key, string Value)>> InfoAsync(string s)
    {
        var d = await Devices.GetAsync(s);
        var df = (await SafeShell(s, "df -h /data | tail -1")).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var mem = await SafeShell(s, "cat /proc/meminfo");
        long Kb(string key) => Regex.Match(mem, key + @":\s+(\d+)") is { Success: true } m ? long.Parse(m.Groups[1].Value) : 0;
        var cpu = await SafeShell(s, "nproc; grep -m1 -iE 'model name|Processor' /proc/cpuinfo | cut -d: -f2");
        var gpu = Regex.Match(await SafeShell(s, "dumpsys SurfaceFlinger 2>/dev/null | grep -m1 GLES"), @"GLES:\s*([^,]+),\s*([^,]+)");
        var media = await Devices.MediaAsync(s);
        var net = await SafeShell(s, "ip -4 -o addr show 2>/dev/null | grep -vE ' lo ' | awk '{print $2\" \"$4}'; dumpsys wifi 2>/dev/null | grep -m1 -oE 'Link speed: [0-9]+Mbps'");
        var mac = (await SafeShell(s, "cat /sys/class/net/eth0/address /sys/class/net/wlan0/address 2>/dev/null | head -1")).Trim();
        string Line(string text, int i) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is var l && l.Length > i ? l[i] : "";
        var bits = d.Abis.Any(a => a.Contains("64")) ? "64-bit" : "32-bit";
        return
        [
            ("Device", d.ProductName),
            ("System", d.OsName),
            ("Model code", $"{d.Prop("ro.product.model")} ({d.Prop("ro.product.device")})"),
            ("Chip", $"{d.Prop("ro.hardware")} · {d.Prop("ro.board.platform")} · {Line(cpu, 0)} cores"),
            ("App compatibility", $"{bits} · {string.Join(" / ", d.Abis)} · SDK {d.Sdk}"),
            ("Graphics", gpu.Success ? $"{gpu.Groups[1].Value.Trim()} {gpu.Groups[2].Value.Trim()}" : "Unknown"),
            ("Memory", Kb("MemTotal") > 0 ? $"{Kb("MemAvailable") / 1024:N0} MB free of {Kb("MemTotal") / 1024:N0} MB" : "Unknown"),
            ("Storage", df.Length >= 5 ? $"{df[2]}B used of {df[1]}B ({df[4]})" : "Unknown"),
            ("Display", $"{(await SafeShell(s, "wm size")).Split(':')[^1].Trim()} · {media.Refresh}"),
            ("HDR", media.Hdr),
            ("Video decoding", media.Video),
            ("Network", string.Join(" · ", net.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) + (mac.Length > 0 ? $" · MAC {mac}" : "")),
            ("Launcher", (await SafeShell(s, "cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.HOME | tail -1")).Split('/')[0]),
            ("Private DNS", await SafeShell(s, "settings get global private_dns_specifier") is { } dns && dns != "null" ? dns : "Off"),
            ("Apps", $"{(await SafeShell(s, "pm list packages -3 | wc -l")).Trim()} installed by you · {(await SafeShell(s, "pm list packages | wc -l")).Trim()} total"),
            ("Security patch", d.Prop("ro.build.version.security_patch")),
            ("Build", d.Prop("ro.build.display.id") is { Length: > 0 } bid ? bid : d.Prop("ro.build.id")),
            ("Uptime", Regex.Replace((await SafeShell(s, "uptime -p")).Replace("up ", ""), @"(^|, )0 \w+", "").TrimStart(',', ' ')),
        ];
    }

    public static async Task<string> NameAsync(string s)
    {
        var name = await SafeShell(s, "settings get global device_name");
        return string.IsNullOrWhiteSpace(name) || name == "null" ? (await PropsAsync(s)).GetValueOrDefault("ro.product.model", s) : name;
    }

    public static async Task<List<AppRow>> AppsAsync(string s, bool includeSystem)
    {
        var all = await Adb.ShellAsync(s, "pm list packages -i" + (includeSystem ? "" : " -3"));
        var disabled = (await Adb.ShellAsync(s, "pm list packages -d")).Split('\n').Select(l => l.Trim().Replace("package:", "")).ToHashSet();
        var system = includeSystem ? (await Adb.ShellAsync(s, "pm list packages -s")).Split('\n').Select(l => l.Trim().Replace("package:", "")).ToHashSet() : [];
        return all.Split('\n')
            .Select(l => Regex.Match(l.Trim(), @"^package:(\S+)\s+installer=(\S*)"))
            .Where(m => m.Success)
            .Select(m => new AppRow(m.Groups[1].Value, m.Groups[2].Value == "null" ? "" : m.Groups[2].Value,
                disabled.Contains(m.Groups[1].Value), system.Contains(m.Groups[1].Value)))
            .OrderBy(r => r.Package).ToList();
    }

    public static async Task<Dictionary<string, string>> InstalledVersionsAsync(string s)
    {
        // versionName of user apps in one round trip
        var text = await Adb.ShellAsync(s, "for p in $(pm list packages -3 | cut -d: -f2); do echo \"$p $(dumpsys package $p | grep -m1 versionName | cut -d= -f2)\"; done", 90);
        return text.Split('\n').Select(l => l.Trim().Split(' ', 2)).Where(p => p.Length == 2)
            .GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.First()[1]);
    }

    public static async Task<string> AppDetailAsync(string s, string pkg)
    {
        Adb.RequirePackage(pkg);
        return await Adb.ShellAsync(s, $"dumpsys package {pkg} | grep -E 'versionName|firstInstallTime|lastUpdateTime|targetSdk' | head -4");
    }

    public static Task<string> LaunchAsync(string s, string pkg)
    {
        Adb.RequirePackage(pkg);
        // Prefer the TV (leanback) launcher entry; fall back to the phone one.
        return Adb.ShellAsync(s, $"monkey -p {pkg} -c android.intent.category.LEANBACK_LAUNCHER 1 2>/dev/null | grep -q 'Events injected: 1' || monkey -p {pkg} -c android.intent.category.LAUNCHER 1");
    }

    public static async Task<string> AppActionAsync(string s, string pkg, string action)
    {
        Adb.RequirePackage(pkg);
        var cmd = action switch
        {
            "stop" => $"am force-stop {pkg}",
            "clear" => $"pm clear {pkg}",
            "enable" => $"pm enable {pkg}",
            "disable" => $"pm disable-user --user 0 {pkg}",
            "uninstall" => $"pm uninstall --user 0 {pkg}",     // system apps: removed for user 0 only, restorable
            "restore" => $"cmd package install-existing {pkg}",
            _ => throw new AdbException($"Unknown action {action}"),
        };
        return await Adb.ShellAsync(s, cmd);
    }

    /// <summary>
    /// Makes pkg the default home app, then presses Home and returns whether it actually opened —
    /// Google TV and Fire TV keep their own home screen on the Home button whatever the default is.
    /// </summary>
    public static async Task<bool> SetLauncherAsync(string s, string pkg)
    {
        Adb.RequirePackage(pkg);
        var comp = (await Adb.ShellAsync(s, $"cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.HOME {pkg} | tail -1")).Trim();
        if (!comp.Contains('/')) throw new AdbException($"{pkg} is not a home-screen launcher");
        await Adb.ShellAsync(s, $"cmd package set-home-activity {comp}");
        var focus = await Adb.ShellAsync(s, "input keyevent KEYCODE_HOME; sleep 3; dumpsys window | grep -m1 mCurrentFocus");
        return focus.Contains($" {pkg}/");
    }

    public const string StockHomeNote = "the default launcher changed, but this TV still opens its own home screen when you press Home. Some launchers have their own \"override Home button\" setting; otherwise open it from your apps.";

    /// <summary>Install .apk, or split bundles (.apks/.xapk/.apkm = zip of APKs).</summary>
    public static async Task<string> InstallAsync(string s, string file, IProgress<string>? progress = null)
    {
        var ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
        var abis = (await Devices.GetAsync(s)).Abis;
        if (ext == ".apk")
        {
            CheckAbi(file, abis);
            progress?.Report($"Installing {System.IO.Path.GetFileName(file)}…");
            return await Explain(() => Adb.RunAsync(["install", "-r", "-d", "-g", file], s, 900));
        }
        if (ext is not (".apks" or ".xapk" or ".apkm")) throw new AdbException("Only .apk, .apks, .xapk and .apkm files can be installed");

        var dir = Directory.CreateTempSubdirectory("tvdeck-");
        try
        {
            progress?.Report("Unpacking bundle…");
            using (var zip = ZipFile.OpenRead(file))
                foreach (var e in zip.Entries.Where(e => e.FullName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains("..")))
                    e.ExtractToFile(System.IO.Path.Combine(dir.FullName, System.IO.Path.GetFileName(e.FullName)));
            var apks = Directory.GetFiles(dir.FullName, "*.apk");
            if (apks.Length == 0) throw new AdbException("The bundle didn't contain any APKs (encrypted .apkm files aren't supported)");
            progress?.Report($"Installing {apks.Length} split APKs…");
            return await Explain(() => Adb.RunAsync(["install-multiple", "-r", "-d", "-g", .. apks], s, 900));
        }
        finally { dir.Delete(true); }
    }

    /// <summary>Show an app's page in the TV's own store (Amazon Appstore on Fire TV, Google Play elsewhere).</summary>
    public static async Task OpenStorePageAsync(string s, string package)
    {
        if (!Adb.PackageName().IsMatch(package)) throw new AdbException($"Bad package name: {package}");
        var uri = (await Devices.GetAsync(s)).IsFireTv ? $"amzn://apps/android?p={package}" : $"https://play.google.com/store/apps/details?id={package}";
        var r = await Adb.ShellAsync(s, $"am start -a android.intent.action.VIEW -d '{uri}'");
        if (r.Contains("Error")) throw new AdbException(r.Trim());
    }

    /// <summary>An APK with native code must include code for one of the TV's ABIs, or it installs and then crashes (or adb refuses).</summary>
    public static void CheckAbi(string apk, IReadOnlyList<string> abis)
    {
        if (abis.Count == 0) return;
        using var zip = ZipFile.OpenRead(apk);
        var libs = zip.Entries.Select(e => e.FullName.Split('/')).Where(p => p.Length > 2 && p[0] == "lib").Select(p => p[1]).ToHashSet();
        if (libs.Count > 0 && !libs.Overlaps(abis))
            throw new AdbException($"This APK is built for {string.Join(", ", libs.Order())}, but this TV runs {string.Join(", ", abis)}. Pick the {abis[0]} build.");
    }

    /// <summary>Turn adb's INSTALL_FAILED_* codes into something a person can act on.</summary>
    static async Task<string> Explain(Func<Task<string>> install)
    {
        try { return await install(); }
        catch (AdbException e)
        {
            var code = Regex.Match(e.Message, @"INSTALL_[A-Z_]+").Value;
            throw new AdbException(code switch
            {
                "INSTALL_FAILED_OLDER_SDK" => "This app needs a newer Android version than the TV has.",
                "INSTALL_FAILED_NO_MATCHING_ABIS" => "This APK isn't built for the TV's processor.",
                "INSTALL_FAILED_INSUFFICIENT_STORAGE" => "The TV is out of storage. Clear some app caches or uninstall something first.",
                "INSTALL_FAILED_UPDATE_INCOMPATIBLE" => "A different signed copy of this app is installed. Uninstall it first (its data will be lost).",
                "INSTALL_FAILED_VERSION_DOWNGRADE" => "The TV already has a newer version.",
                "INSTALL_FAILED_VERIFICATION_FAILURE" => "Play Protect on the TV blocked this app (it also blocks apps built for old Android versions).",
                "INSTALL_FAILED_MISSING_FEATURE" or "INSTALL_FAILED_MISSING_SHARED_LIBRARY" => "This app needs hardware or Google services the TV doesn't have.",
                "INSTALL_PARSE_FAILED_NO_CERTIFICATES" or "INSTALL_PARSE_FAILED_NOT_APK" => "The file is damaged or isn't an APK.",
                _ => e.Message,
            } + (code.Length > 0 ? $" ({code})" : ""));
        }
    }

    public static async Task<List<string>> ApkPathsAsync(string s, string pkg)
    {
        Adb.RequirePackage(pkg);
        return (await Adb.ShellAsync(s, $"pm path {pkg}")).Split('\n').Select(l => l.Trim())
            .Where(l => l.StartsWith("package:")).Select(l => l[8..]).ToList();
    }

    /// <summary>Copy an app's APK(s) to the PC. Returns the folder.</summary>
    public static async Task<string> BackupAsync(string s, IEnumerable<string> packages, string dest, IProgress<string>? progress = null)
    {
        var failed = new List<string>();
        foreach (var pkg in packages)
        {
            progress?.Report($"Saving {pkg}…");
            try
            {
                var d = Directory.CreateDirectory(System.IO.Path.Combine(dest, pkg)).FullName;
                var paths = await ApkPathsAsync(s, pkg);
                for (var i = 0; i < paths.Count; i++)
                    await Adb.RunAsync(["pull", paths[i], System.IO.Path.Combine(d, i == 0 ? "base.apk" : System.IO.Path.GetFileName(paths[i]))], s, 600);
            }
            catch (AdbException e) { failed.Add($"{pkg}: {e.Message}"); }
        }
        if (failed.Count > 0) throw new AdbException($"Saved to {dest}, but {failed.Count} failed:\n" + string.Join('\n', failed));
        return dest;
    }

    public static async Task<(int Ok, List<string> Failed)> RestoreAsync(string s, string folder, IProgress<string>? progress = null)
    {
        var ok = 0; var failed = new List<string>();
        foreach (var d in Directory.GetDirectories(folder).Order())
        {
            var apks = Directory.GetFiles(d, "*.apk").Order().ToArray();
            if (apks.Length == 0) continue;
            progress?.Report($"Installing {System.IO.Path.GetFileName(d)}…");
            try
            {
                await Adb.RunAsync([apks.Length > 1 ? "install-multiple" : "install", "-r", "-d", .. apks], s, 900);
                ok++;
            }
            catch (AdbException e) { failed.Add($"{System.IO.Path.GetFileName(d)}: {e.Message}"); }
        }
        return (ok, failed);
    }

    public static async Task<List<FileRow>> ListAsync(string s, string path)
    {
        var text = await Adb.ShellAsync(s, $"ls -1pa {Adb.Q(path)}");
        return text.Split('\n').Select(l => l.TrimEnd('\r')).Where(e => e is not ("" or "./" or "../"))
            .Select(e => new FileRow(e.TrimEnd('/'), e.EndsWith('/')))
            .OrderByDescending(f => f.IsDir).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static Task<string> PushAsync(string s, string local, string remoteDir)
        => Adb.RunAsync(["push", local, remoteDir.TrimEnd('/') + "/" + System.IO.Path.GetFileName(local)], s, 1800);

    public static Task<string> PullAsync(string s, string remote, string local)
        => Adb.RunAsync(["pull", remote, local], s, 1800);

    public static Task<string> DeleteAsync(string s, string remote)
        => Adb.ShellAsync(s, $"rm -rf {Adb.Q(remote)}");

    public static Task<string> KeyAsync(string s, string keycode, bool longPress = false)
    {
        if (!Regex.IsMatch(keycode, "^[A-Z0-9_]+$")) throw new AdbException("bad key");
        return Adb.ShellAsync(s, $"input keyevent {(longPress ? "--longpress " : "")}KEYCODE_{keycode}");
    }

    public static Task<string> TextAsync(string s, string text)
    {
        if (string.IsNullOrEmpty(text)) throw new AdbException("Type some text first");
        return Adb.ShellAsync(s, "input text " + Adb.Q(text.Replace("%", "\\%").Replace(" ", "%s")));
    }

    public static async Task<byte[]> ScreenshotAsync(string s)
    {
        var (code, png, err) = await Adb.RunRawAsync(["exec-out", "screencap", "-p"], s, 30);
        if (code != 0 || png.Length < 8 || png[0] != 0x89) throw new AdbException("Screenshot failed — the TV may be asleep or showing protected (DRM) video. " + err);
        return png;
    }

    public static Task<string> RebootAsync(string s, string mode = "") =>
        Adb.RunAsync(mode == "" ? ["reboot"] : ["reboot", mode], s);
}
