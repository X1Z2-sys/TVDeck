using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TVDeck.Core;

/// <summary>What kind of TV software the device runs. Drives tweak filtering and catalog notes.</summary>
public enum Platform { FireTv, GoogleTv, Shield, AndroidTv, Android }

/// <summary>Model code → marketing name (Assets/devices.json).</summary>
public sealed class DeviceModel
{
    /// <summary>Exact value (case-insensitive), or a prefix when it ends with '*'.</summary>
    [JsonPropertyName("match")] public string Match { get; set; } = "";
    /// <summary>Which property to compare: model (ro.product.model) or device (ro.product.device).</summary>
    [JsonPropertyName("field")] public string Field { get; set; } = "model";
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    public bool Matches(string model, string device)
    {
        var v = Field == "device" ? device : model;
        return Match.EndsWith('*') ? v.StartsWith(Match[..^1], StringComparison.OrdinalIgnoreCase) : v.Equals(Match, StringComparison.OrdinalIgnoreCase);
    }
}

[JsonSerializable(typeof(List<DeviceModel>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class DeviceJson : JsonSerializerContext;

/// <summary>Everything we know about a connected device, read once per connection.</summary>
public sealed record DeviceProfile(
    string Serial,
    IReadOnlyDictionary<string, string> Props,
    Platform Platform,
    string ProductName,     // "Fire TV Edition smart TV", "SHIELD TV Pro", …
    IReadOnlyList<string> Abis,
    int Sdk)
{
    public string Prop(string key) => Props.GetValueOrDefault(key, "");
    public bool IsFireTv => Platform == Platform.FireTv;

    public string OsName => Platform == Platform.FireTv && Prop("ro.build.mktg.fireos") is { Length: > 0 } f ? $"{f} (Android {Prop("ro.build.version.release")})"
        : $"{PlatformName} · Android {Prop("ro.build.version.release")}";

    public string PlatformName => Platform switch
    {
        Platform.FireTv => "Fire OS", Platform.GoogleTv => "Google TV", Platform.Shield => "SHIELD Experience",
        Platform.AndroidTv => "Android TV", _ => "Android",
    };

    /// <summary>The TV brand/model a Fire TV Edition or OEM Android TV ships as (e.g. INSIGNIA NS-50F502NA26), if it says.</summary>
    public string? OemModel => Prop("ro.product.oemmodel") is { Length: > 0 } m ? m.Replace('_', ' ') : null;

    /// <summary>Who made the hardware: the TV maker on Fire TV Edition / OEM sets (INSIGNIA_NS-… → "INSIGNIA"), else the manufacturer.</summary>
    public string Brand => (OemModel?.Split(' ', '-')[0] is { Length: > 1 } b ? b : Prop("ro.product.manufacturer")).Trim();

    /// <summary>
    /// The brand's logo: Assets/Brands/{brand}.svg|png|jpg, else a name from Brands/aliases.json (e.g. "Koninklijke Philips" → philips.svg).
    /// Credits in CREDITS.txt. The drawn generic TV when we have nothing.
    /// </summary>
    public string PhotoPath
    {
        get
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Assets", "Brands");
            var key = new string(Brand.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            return new[] { ".png", ".jpg", ".svg" }.Select(ext => Path.Combine(dir, key + ext)).FirstOrDefault(File.Exists)
                ?? (BrandAliases.Value.TryGetValue(key, out var file) ? Path.Combine(dir, file) : null)
                ?? Path.Combine(dir, "generic.png");
        }
    }

    static readonly Lazy<Dictionary<string, string>> BrandAliases = new(() =>
    {
        var f = Path.Combine(AppContext.BaseDirectory, "Assets", "Brands", "aliases.json");
        return File.Exists(f) ? JsonSerializer.Deserialize(File.ReadAllText(f), DeviceJson.Default.DictionaryStringString) ?? [] : [];
    });
}

public static partial class Devices
{
    static List<DeviceModel>? _models;
    static List<DeviceModel> Models => _models ??= JsonSerializer.Deserialize(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "devices.json")), DeviceJson.Default.ListDeviceModel) ?? [];

    static readonly Dictionary<string, DeviceProfile> Cache = [];
    // Connect/disconnect can put a different TV behind the same address: re-read on any device-list change.
    static Devices() => Session.DevicesInvalidated += () => { lock (Cache) Cache.Clear(); };

    /// <summary>Profile for a serial; cached because none of it changes while connected (a reboot re-connects).</summary>
    public static async Task<DeviceProfile> GetAsync(string serial)
    {
        lock (Cache) if (Cache.TryGetValue(serial, out var hit)) return hit;
        var props = await Tv.PropsAsync(serial);
        var markers = await Tv.SafeShell(serial, "pm list features; pm list packages com.google.android.apps.tv.launcherx");
        var profile = Build(serial, props, markers);
        lock (Cache) Cache[serial] = profile;
        return profile;
    }

    /// <summary>Pure classification, separated from adb for testing.</summary>
    public static DeviceProfile Build(string serial, IReadOnlyDictionary<string, string> p, string featuresAndPackages)
    {
        string P(string k) => p.GetValueOrDefault(k, "");
        var maker = P("ro.product.manufacturer");
        var platform =
            maker.Equals("Amazon", StringComparison.OrdinalIgnoreCase) || featuresAndPackages.Contains("amazon.hardware.fire_tv") ? Platform.FireTv
            : maker.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase) ? Platform.Shield
            : featuresAndPackages.Contains("com.google.android.apps.tv.launcherx") ? Platform.GoogleTv
            : featuresAndPackages.Contains("android.software.leanback") ? Platform.AndroidTv
            : Platform.Android;

        var model = P("ro.product.model");
        var device = P("ro.product.device");
        var known = Models.FirstOrDefault(m => m.Matches(model, device));
        var name = known?.Name ?? platform switch
        {
            Platform.FireTv => P("ro.product.oemmodel") is { Length: > 0 } ? "Fire TV Edition smart TV" : "Fire TV",
            Platform.Shield => "NVIDIA SHIELD TV",
            _ => $"{maker} {model}".Trim(),
        };

        var abis = P("ro.product.cpu.abilist").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (abis.Count == 0 && P("ro.product.cpu.abi") is { Length: > 0 } a) abis.Add(a);
        return new DeviceProfile(serial, p, platform, name, abis, int.TryParse(P("ro.build.version.sdk"), out var sdk) ? sdk : 0);
    }

    /// <summary>Shared "platforms" filter for tweaks and catalog apps.</summary>
    public static bool Applies(string? platforms, Platform p) => platforms switch
    {
        "firetv" => p == Platform.FireTv,
        "googletv" => p == Platform.GoogleTv,
        "shield" => p == Platform.Shield,
        "google" => p != Platform.FireTv,
        _ => true,
    };

    /// <summary>Hardware video decoders, HDR formats and refresh rates — what the TV can actually play smoothly.</summary>
    public static async Task<(string Video, string Hdr, string Refresh)> MediaAsync(string serial)
    {
        var codecs = await Tv.SafeShell(serial, "cat /vendor/etc/media_codecs*.xml /odm/etc/media_codecs*.xml 2>/dev/null | grep -oE 'MediaCodec name=\"[^\"]+\"' | sort -u");
        // Hardware decoders only: skip Google's software codecs and encoders.
        var hw = Regex.Matches(codecs, "name=\"([^\"]+)\"").Select(m => m.Groups[1].Value.ToLowerInvariant())
            .Where(n => n.Contains("decoder") && !n.StartsWith("c2.android") && !n.StartsWith("omx.google")).ToList();
        bool Has(params string[] keys) => hw.Any(n => keys.Any(n.Contains));
        var video = string.Join(" · ", new[] { ("AV1", Has("av1")), ("HEVC", Has("hevc")), ("VP9", Has("vp9")), ("H.264", Has("avc", "h264")), ("Dolby Vision", Has(".dv", "dolby")) }
            .Where(x => x.Item2).Select(x => x.Item1));

        var display = await Tv.SafeShell(serial, "dumpsys display | grep -m1 -oE 'mSupportedHdrTypes=\\[[0-9, ]*\\]'; dumpsys display | grep -oE 'fps=[0-9.]+' | sort -u");
        var hdrTypes = Regex.Match(display, @"\[([0-9, ]*)\]").Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hdr = string.Join(" · ", hdrTypes.Select(t => t switch { "1" => "Dolby Vision", "2" => "HDR10", "3" => "HLG", "4" => "HDR10+", _ => null }).OfType<string>());
        var rates = Regex.Matches(display, @"fps=([0-9.]+)").Select(m => (int)Math.Round(double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)))
            .Distinct().Order().Select(r => $"{r} Hz");
        return (video.Length > 0 ? video : "Unknown", hdr.Length > 0 ? hdr : "None reported", string.Join(" · ", rates) is { Length: > 0 } rr ? rr : "Unknown");
    }
}
