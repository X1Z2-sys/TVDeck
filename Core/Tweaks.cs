using System.Text.Json;
using System.Text.Json.Serialization;

namespace TVDeck.Core;

/// <summary>A reversible setting: commands to apply, commands to undo, and a read command whose output says if it's on.</summary>
public sealed class Tweak
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("group")] public string Group { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    /// <summary>"all", "firetv", "googletv", "shield", or "google" (anything that isn't Fire TV).</summary>
    [JsonPropertyName("platforms")] public string Platforms { get; set; } = "all";
    [JsonPropertyName("risk")] public string Risk { get; set; } = "low";
    [JsonPropertyName("apply")] public List<string> Apply { get; set; } = [];
    [JsonPropertyName("undo")] public List<string> Undo { get; set; } = [];
    /// <summary>Shell command; if its trimmed output equals <see cref="OnValue"/> the tweak counts as on.</summary>
    [JsonPropertyName("check")] public string? Check { get; set; }
    [JsonPropertyName("on")] public string? OnValue { get; set; }
    /// <summary>For choice tweaks: label → value substituted into {v} in Apply. A value of "" means "Default" and runs Undo.</summary>
    [JsonPropertyName("choices")] public Dictionary<string, string>? Choices { get; set; }
    /// <summary>Set on tweaks TVDeck recommends for everyone (low risk, reversible, nothing lost): "on", or the choice value.</summary>
    [JsonPropertyName("recommend")] public string? Recommend { get; set; }
    /// <summary>Package that must be installed for this tweak to make sense (hidden otherwise).</summary>
    [JsonPropertyName("requires")] public string? Requires { get; set; }

    /// <summary>No on/off state: a one-time action shown as a button.</summary>
    public bool IsAction => Check is null && Choices is null;

    public bool Applies(Platform p) => Devices.Applies(Platforms, p);

    /// <summary>Usable on this TV: right platform and any required app installed.</summary>
    public bool Applies(Platform p, ISet<string> installed) => Applies(p) && (Requires is null || installed.Contains(Requires));

    /// <summary>The choice label that puts things back (the one equal to Undo, or the "" Default).</summary>
    public string? DefaultChoice => Choices?.FirstOrDefault(kv => kv.Value == "" ||
        Apply.Select(c => c.Replace("{v}", kv.Value)).SequenceEqual(Undo)).Key;

    /// <summary>Plain-language "how do I undo this".</summary>
    public string UndoHint =>
        IsAction ? (Undo.Count == 0 ? "One-time action — nothing to undo." : "Undo: press Undo.") :
        Choices is not null ? (DefaultChoice is { } d ? $"Undo: choose “{d}”." : "Undo: restore a snapshot on the Backup page.") :
        "Undo: switch it off.";

    /// <summary>
    /// True when Recommend has nothing to do: already matches, or (for choices) the user picked their own non-default value —
    /// a recommendation only replaces defaults, never a deliberate choice like "animations off".
    /// </summary>
    public bool IsRecommendedState(string? current)
    {
        if (Choices is null) return current == OnValue;
        if (SameValue(Recommend ?? "", current)) return true;
        var unset = string.IsNullOrEmpty(current) || current == "null";
        var defaultValue = DefaultChoice is { } d ? Choices[d] : null;
        return !unset && defaultValue is not null && defaultValue != "" && !SameValue(defaultValue, current);
    }

    /// <summary>TVs store "0" for "0.0" etc., so compare numerically when both sides are numbers.</summary>
    public static bool SameValue(string a, string? b) => a == b ||
        (double.TryParse(a, System.Globalization.CultureInfo.InvariantCulture, out var x) &&
         double.TryParse(b, System.Globalization.CultureInfo.InvariantCulture, out var y) && x == y);
}

[JsonSerializable(typeof(List<Tweak>))]
[JsonSerializable(typeof(List<Debloater>))]
internal partial class TweakJson : JsonSerializerContext;

public static class Tweaks
{
    static List<Tweak>? _all;
    public static List<Tweak> All => _all ??= JsonSerializer.Deserialize(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "tweaks.json")), TweakJson.Default.ListTweak) ?? [];

    public static Tweak? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    /// <summary>Installed package names, for <see cref="Tweak.Requires"/>.</summary>
    public static async Task<HashSet<string>> InstalledAsync(string serial) =>
        (await Adb.ShellAsync(serial, "pm list packages")).Split('\n')
            .Select(l => l.Trim()).Where(l => l.StartsWith("package:")).Select(l => l[8..]).ToHashSet();

    /// <summary>Reads every check in one adb round-trip; returns id → current output.</summary>
    public static async Task<Dictionary<string, string>> ReadAsync(string serial, IEnumerable<Tweak> tweaks)
    {
        var list = tweaks.Where(t => t.Check is not null).ToList();
        if (list.Count == 0) return [];
        var script = string.Join("; ", list.Select(t => $"echo \"@@{t.Id}=$({t.Check} 2>/dev/null)\""));
        var output = await Adb.ShellAsync(serial, script);
        var result = new Dictionary<string, string>();
        foreach (var line in output.Split('\n'))
        {
            var l = line.Trim();
            if (!l.StartsWith("@@")) continue;
            var eq = l.IndexOf('=');
            if (eq > 2) result[l[2..eq]] = l[(eq + 1)..].Trim();
        }
        return result;
    }

    public static async Task SetAsync(string serial, Tweak t, bool on, string? choice = null)
    {
        var cmds = on ? t.Apply : t.Undo;
        if (choice is not null)
        {
            if (t.Choices is null || !t.Choices.ContainsValue(choice)) throw new AdbException("Unknown option.");
            cmds = choice == "" ? t.Undo : cmds.Select(c => c.Replace("{v}", choice)).ToList();
        }
        await Snapshots.EnsureAsync(serial);
        foreach (var c in cmds)
        {
            // One-time actions (app optimization) can take minutes; settings writes are instant.
            var output = await Adb.ShellAsync(serial, c + "; echo \"@@rc=$?\"", t.IsAction ? 900 : 60);
            var rc = output.Split('\n').LastOrDefault(l => l.StartsWith("@@rc="))?.Trim()[5..];
            if (rc is not "0")
                throw new AdbException($"The TV refused: {c}\n{output.Replace($"@@rc={rc}", "").Trim()}");
        }
    }
}
