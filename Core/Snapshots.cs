using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TVDeck.Core;

/// <summary>
/// What a TV looked like before TVDeck changed it: the value of every setting TVDeck can change, and which apps were
/// disabled. Only those keys are saved — never the whole settings store, which holds device IDs and other private values.
/// </summary>
public sealed class Snapshot
{
    [JsonPropertyName("tv")] public string Tv { get; set; } = "";
    [JsonPropertyName("serialno")] public string SerialNo { get; set; } = "";
    [JsonPropertyName("taken")] public DateTime Taken { get; set; }
    /// <summary>"namespace/key" → value; null means the key wasn't set. Only keys listed here are ever restored.</summary>
    [JsonPropertyName("settings")] public Dictionary<string, string?> Settings { get; set; } = [];
    [JsonPropertyName("disabled")] public List<string> Disabled { get; set; } = [];
}

[JsonSerializable(typeof(Snapshot))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class SnapshotJson : JsonSerializerContext;

public static partial class Snapshots
{
    public sealed record Change(string What, string Command);

    public static string Dir => Directory.CreateDirectory(Path.Combine(Tools.DataDir, "Snapshots")).FullName;

    static readonly HashSet<string> Done = [];
    static readonly SemaphoreSlim Gate = new(1, 1);

    [GeneratedRegex(@"settings (?:put|delete) (global|secure|system) ([\w.:\-]+)")] internal static partial Regex SettingKey();
    [GeneratedRegex(@"^(global|secure|system)/[\w.:\-]+$")] internal static partial Regex SafeKey();

    /// <summary>Every setting a tweak or recipe can touch, as "namespace/key".</summary>
    static List<string> OwnedKeys() => Tweaks.All.SelectMany(t => t.Apply.Concat(t.Undo))
        .Concat(Catalog.AllRecipes().SelectMany(r => r.Steps).Select(s => s.Shell ?? ""))
        .SelectMany(c => SettingKey().Matches(c)).Select(m => $"{m.Groups[1].Value}/{m.Groups[2].Value}")
        .Distinct().Order().ToList();

    /// <summary>Saves one automatic snapshot per TV per run, before the first change. Throws (so nothing changes) if it can't.</summary>
    public static async Task EnsureAsync(string serial)
    {
        await Gate.WaitAsync();
        try
        {
            if (Done.Contains(serial)) return;
            try { await SaveAsync(serial, "before changes"); }
            catch (Exception ex) when (ex is AdbException or IOException)
            {
                throw new AdbException($"Couldn't save a settings snapshot first, so nothing was changed.\n{ex.Message}");
            }
            Done.Add(serial);
        }
        finally { Gate.Release(); }
    }

    public static async Task<string> SaveAsync(string serial, string reason)
    {
        var snap = await ReadAsync(serial, OwnedKeys());
        var name = Tools.SafeFileName($"{snap.Tv} {snap.Taken:yyyy-MM-dd HHmmss} {reason}") + ".json";
        var path = Path.Combine(Dir, name);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(snap, SnapshotJson.Default.Snapshot));
        return path;
    }

    public static List<string> List() =>
        Directory.GetFiles(Dir, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).ToList();

    public static Snapshot Load(string path) =>
        JsonSerializer.Deserialize(File.ReadAllText(path), SnapshotJson.Default.Snapshot) ?? throw new IOException("Empty snapshot file");

    static async Task<Snapshot> ReadAsync(string serial, List<string> keys)
    {
        // One round-trip: serial, each setting, then the disabled-app list.
        var script = "echo \"@@serial=$(getprop ro.serialno)\"; " +
            string.Concat(keys.Select(k => $"echo \"@@{k}=$(settings get {k.Replace('/', ' ')})\"; ")) +
            "echo @@disabled; pm list packages -d";
        var output = await Adb.ShellAsync(serial, script);
        var snap = new Snapshot { Tv = await Tv.NameAsync(serial), Taken = DateTime.Now };
        var inDisabled = false;
        foreach (var raw in output.Split('\n'))
        {
            var l = raw.Trim();
            if (l == "@@disabled") { inDisabled = true; continue; }
            if (inDisabled)
            {
                if (l.StartsWith("package:") && Adb.PackageName().IsMatch(l[8..])) snap.Disabled.Add(l[8..]);
                continue;
            }
            if (!l.StartsWith("@@")) continue;
            var eq = l.IndexOf('=');
            if (eq < 3) continue;
            var (k, v) = (l[2..eq], l[(eq + 1)..]);
            if (k == "serial") snap.SerialNo = v;
            else if (keys.Contains(k)) snap.Settings[k] = v is "null" or "" ? null : v;
        }
        snap.Disabled.Sort();
        return snap;
    }

    /// <summary>What restoring <paramref name="snap"/> would change on the TV right now (empty = already matches).</summary>
    public static async Task<List<Change>> PlanAsync(string serial, Snapshot snap)
    {
        // Snapshot files are plain JSON on disk: only well-formed keys and package names reach the shell.
        var keys = snap.Settings.Keys.Where(k => SafeKey().IsMatch(k)).ToList();
        var wasDisabled = snap.Disabled.Where(p => Adb.PackageName().IsMatch(p)).ToList();
        var now = await ReadAsync(serial, keys);
        var changes = new List<Change>();
        foreach (var key in keys)
        {
            var was = snap.Settings[key];
            if (!now.Settings.TryGetValue(key, out var cur) || cur == was) continue;
            var (ns, name) = (key[..key.IndexOf('/')], key[(key.IndexOf('/') + 1)..]);
            changes.Add(was is null
                ? new($"{name}: clear (now {cur})", $"settings delete {ns} {name}")
                : new($"{name}: {cur ?? "not set"} → {was}", $"settings put {ns} {name} {Adb.Q(was)}"));
        }
        foreach (var p in now.Disabled.Except(wasDisabled)) changes.Add(new($"Enable {p}", $"pm enable {p}"));
        foreach (var p in wasDisabled.Except(now.Disabled)) changes.Add(new($"Disable {p}", $"pm disable-user --user 0 {p}"));
        return changes;
    }

    /// <summary>Saves the current state first (so a restore can itself be undone), then applies every change. Returns the ones the TV refused.</summary>
    public static async Task<List<string>> RestoreAsync(string serial, List<Change> changes)
    {
        await SaveAsync(serial, "before restore");
        var failed = new List<string>();
        foreach (var c in changes)
        {
            var output = await Adb.ShellAsync(serial, c.Command + "; echo \"@@rc=$?\"");
            if (!output.Contains("@@rc=0")) failed.Add(c.What);
        }
        return failed;
    }
}
