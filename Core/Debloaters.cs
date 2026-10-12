// TVDeck "Debloater" tab: curated debloat lists/tools from GitHub, loaded from Assets/debloaters.json.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TVDeck.Core;

public sealed record Debloater(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("author")] string Author,
    /// <summary>"owner/name" on GitHub.</summary>
    [property: JsonPropertyName("repo")] string Repo,
    [property: JsonPropertyName("desc")] string Desc,
    [property: JsonPropertyName("platforms")] string[] Platforms,
    /// <summary>Recommended groups: label → packages to disable via pm disable-user.</summary>
    [property: JsonPropertyName("groups")] Dictionary<string, string[]> Groups,
    /// <summary>Optional groups with functional trade-offs, confirm-guarded in the UI.</summary>
    [property: JsonPropertyName("optin")] Dictionary<string, string[]> OptIn,
    /// <summary>Warnings shown under the card.</summary>
    [property: JsonPropertyName("notes")] string[]? Notes = null,
    /// <summary>Only offered on these TV brands (normalized, e.g. "tcl"); null = any brand.</summary>
    [property: JsonPropertyName("brands")] string[]? Brands = null);

/// <summary>Which packages the TV has, and which of those are disabled.</summary>
public sealed record PackageState(HashSet<string> Installed, HashSet<string> Disabled);

public static class Debloaters
{
    static List<Debloater>? _all;
    public static List<Debloater> All => _all ??= JsonSerializer.Deserialize(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "debloaters.json")), TweakJson.Default.ListDebloater) ?? [];

    public static bool Applies(Debloater d, Platform p, string brand) =>
        ((d.Platforms ?? []).Length == 0 || d.Platforms!.Contains("all") ||
         d.Platforms.Contains(p switch { Platform.FireTv => "firetv", Platform.Shield => "shield", _ => "googletv" })) &&
        (d.Brands is null || d.Brands.Contains(new string(brand.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray())));

    /// <summary>One round-trip: every package on the TV, and the disabled ones.</summary>
    public static async Task<PackageState> ReadStateAsync(string serial)
    {
        var output = await Adb.ShellAsync(serial, "pm list packages; echo @@disabled; pm list packages -d");
        var installed = new HashSet<string>();
        var disabled = new HashSet<string>();
        var target = installed;
        foreach (var raw in output.Split('\n'))
        {
            var l = raw.Trim();
            if (l == "@@disabled") target = disabled;
            else if (l.StartsWith("package:")) target.Add(l[8..]);
        }
        return new(installed, disabled);
    }

    /// <summary>
    /// Disables (pm disable-user) or re-enables packages in one round-trip, after the safety snapshot.
    /// Returns the packages the TV refused — pm prints "new state: …" only when it actually changed something.
    /// </summary>
    public static async Task<List<string>> SetAsync(string serial, IReadOnlyCollection<string> packages, bool disable)
    {
        if (packages.Count == 0) return [];
        foreach (var p in packages) Adb.RequirePackage(p);   // lists come from a JSON file; never pass raw text to the shell
        await Snapshots.EnsureAsync(serial);
        var verb = disable ? "disable-user" : "enable";
        var script = string.Join("; ", packages.Select(p => $"echo @@{p}; pm {verb} --user 0 {p} 2>&1"));
        var output = await Adb.ShellAsync(serial, script, 30 + packages.Count * 5);
        var refused = new List<string>();
        string? current = null;
        var ok = false;
        foreach (var raw in output.Split('\n').Append("@@"))
        {
            var l = raw.Trim();
            if (l.StartsWith("@@"))
            {
                if (current is not null && !ok) refused.Add(current);
                current = l.Length > 2 ? l[2..] : null;
                ok = false;
            }
            else if (l.Contains("new state:")) ok = true;
        }
        return refused;
    }
}
