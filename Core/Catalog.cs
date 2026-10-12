using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TVDeck.Core;

/// <summary>Where the newest APK for an app comes from.</summary>
public sealed class AppSource
{
    /// <summary>github | fdroid | url | index (newest .apk link on an official download page)</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("repo")] public string? Repo { get; set; }
    /// <summary>Regex matched against GitHub asset names (case-insensitive).</summary>
    [JsonPropertyName("asset")] public string? Asset { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }

    public bool Installable => Type is "github" or "fdroid" or "url" or "index";
}

public sealed class CatalogApp
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "Other";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("package")] public string Package { get; set; } = "";
    [JsonPropertyName("homepage")] public string? Homepage { get; set; }
    [JsonPropertyName("gray")] public bool Gray { get; set; }
    /// <summary>Which TVs it suits (see Devices.Applies); hidden in the store on others.</summary>
    [JsonPropertyName("platforms")] public string? Platforms { get; set; }
    [JsonPropertyName("source")] public AppSource Source { get; set; } = new();
    /// <summary>adb shell commands to run after install (permissions, defaults). Shown to the user before running.</summary>
    [JsonPropertyName("after")] public List<string> After { get; set; } = [];

    [JsonIgnore] public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
    [JsonIgnore] public string SourceLabel => Source.Type switch
    {
        "github" => $"GitHub · {Source.Repo}",
        "fdroid" => "F-Droid",
        "url" or "index" => new Uri(Source.Url!.Split(' ')[0]).Host,
        _ => Source.Note ?? "Store only",
    };
}

/// <summary>A named, shareable install script: a list of steps run against the selected TV.</summary>
public sealed class Recipe
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("steps")] public List<RecipeStep> Steps { get; set; } = [];
}

/// <summary>Exactly one of the fields is set.</summary>
public sealed class RecipeStep
{
    /// <summary>Install a catalog app by id.</summary>
    [JsonPropertyName("install")] public string? Install { get; set; }
    /// <summary>Install an app not in the catalog.</summary>
    [JsonPropertyName("app")] public CatalogApp? App { get; set; }
    /// <summary>Run an adb shell command.</summary>
    [JsonPropertyName("shell")] public string? Shell { get; set; }
    /// <summary>Make this package the home-screen launcher.</summary>
    [JsonPropertyName("launcher")] public string? Launcher { get; set; }
    /// <summary>Open this package.</summary>
    [JsonPropertyName("launch")] public string? Launch { get; set; }
    /// <summary>Apply a TVDeck tweak by id; choice tweaks also need <see cref="Value"/>. Skipped on TVs it doesn't apply to.</summary>
    [JsonPropertyName("tweak")] public string? Tweak { get; set; }
    [JsonPropertyName("value")] public string? Value { get; set; }

    public string Describe(Func<string, CatalogApp?> lookup) =>
        Install is not null ? $"Install {lookup(Install)?.Name ?? Install}" :
        App is not null ? $"Install {App.Name} ({App.SourceLabel})" :
        Shell is not null ? $"Run: {Shell}" :
        Launcher is not null ? $"Set {Launcher} as the home screen" :
        Launch is not null ? $"Open {Launch}" :
        Tweak is not null ? (Tweaks.Find(Tweak) is not { } t ? $"Tweak {Tweak}" :
            t.Choices is not null ? $"{t.Title}: {t.Choices.FirstOrDefault(kv => kv.Value == Value).Key ?? Value}" :
            t.IsAction ? t.Title : $"Turn on: {t.Title}") :
        "(empty step)";
}

[JsonSerializable(typeof(List<CatalogApp>))]
[JsonSerializable(typeof(Recipe))]
[JsonSerializable(typeof(List<Recipe>))]
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
public partial class CatalogJson : JsonSerializerContext;

public static class Catalog
{
    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TVDeck", "1.0"));
        if (Environment.GetEnvironmentVariable("GITHUB_TOKEN") is { Length: > 0 } t)
            h.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", t);
        return h;
    }

    public static string RecipesDir => Directory.CreateDirectory(Path.Combine(Tools.DataDir, "Recipes")).FullName;
    static string UserAppsFile => Path.Combine(Tools.DataDir, "my-apps.json");
    static string CacheDir => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "TVDeck", "apk-cache")).FullName;

    static List<CatalogApp>? _apps;

    /// <summary>Built-in catalog (Assets/catalog.json) plus the user's own additions.</summary>
    public static List<CatalogApp> Apps => _apps ??= Load();

    static List<CatalogApp> Load()
    {
        var builtIn = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "catalog.json")), CatalogJson.Default.ListCatalogApp) ?? [];
        var mine = File.Exists(UserAppsFile) ? JsonSerializer.Deserialize(File.ReadAllText(UserAppsFile), CatalogJson.Default.ListCatalogApp) ?? [] : [];
        return [.. builtIn.Where(b => mine.All(m => m.Id != b.Id)), .. mine];
    }

    public static CatalogApp? Find(string id) => Apps.FirstOrDefault(a => a.Id == id);

    public static void AddUserApp(CatalogApp app)
    {
        Validate(app);
        var mine = File.Exists(UserAppsFile) ? JsonSerializer.Deserialize(File.ReadAllText(UserAppsFile), CatalogJson.Default.ListCatalogApp) ?? [] : [];
        mine.RemoveAll(a => a.Id == app.Id);
        mine.Add(app);
        File.WriteAllText(UserAppsFile, JsonSerializer.Serialize(mine, CatalogJson.Default.ListCatalogApp));
        _apps = null;
    }

    public static void Validate(CatalogApp app)
    {
        if (string.IsNullOrWhiteSpace(app.Name)) throw new InvalidOperationException("Give the app a name");
        if (app.Package.Length > 0) Adb.RequirePackage(app.Package);
        switch (app.Source.Type)
        {
            case "github" when !Regex.IsMatch(app.Source.Repo ?? "", @"^[\w.-]+/[\w.-]+$"):
                throw new InvalidOperationException("GitHub source must look like owner/repo");
            case "fdroid" when !Adb.PackageName().IsMatch(app.Source.Id ?? ""):
                throw new InvalidOperationException("F-Droid source needs the app's package id");
            case "url" or "index" when (app.Source.Url ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries) is not { Length: > 0 } urls
                                       || urls.Any(x => !Uri.TryCreate(x, UriKind.Absolute, out var u) || u.Scheme != "https"):
                throw new InvalidOperationException("Download links must start with https://");
        }
        if (app.Source.Asset is { } rx) _ = new Regex(rx); // throws on a bad pattern
    }

    // ---------- resolving and downloading ----------

    public sealed record Resolved(string Url, string FileName, string Version);

    /// <summary>Find the newest APK for the app that suits the device's CPU (abis in preference order).</summary>
    public static async Task<Resolved> ResolveAsync(CatalogApp app, IReadOnlyList<string> abis, CancellationToken ct = default)
    {
        var src = app.Source;
        switch (src.Type)
        {
            case "github":
            {
                var rx = new Regex(src.Asset ?? @"\.apk$", RegexOptions.IgnoreCase);
                JsonArray json;
                try { json = JsonNode.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{src.Repo}/releases?per_page=15", ct))!.AsArray(); }
                catch (HttpRequestException e) when (e.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests)
                {
                    // API rate limit (60/h without a token): read the public release page instead.
                    using var latest = await Http.GetAsync($"https://github.com/{src.Repo}/releases/latest", ct);
                    var tag = latest.RequestMessage!.RequestUri!.AbsolutePath.Split("/tag/") is [_, var t] ? Uri.UnescapeDataString(t) : throw new InvalidOperationException($"{src.Repo} has no releases");
                    var html = await Http.GetStringAsync($"https://github.com/{src.Repo}/releases/expanded_assets/{Uri.EscapeDataString(tag)}", ct);
                    var assets = Regex.Matches(html, @"href=""(/[^""]+/releases/download/[^""]+\.apk)""").Select(m => m.Groups[1].Value).Distinct()
                        .Select(p => (Name: Uri.UnescapeDataString(p[(p.LastIndexOf('/') + 1)..]), Url: "https://github.com" + p))
                        .Where(a => rx.IsMatch(a.Name)).ToList();
                    var pick = PickForAbi(assets, abis, a => a.Name);
                    return pick != default ? new Resolved(pick.Url, pick.Name, tag) : throw new InvalidOperationException($"No compatible APK in {src.Repo} {tag}");
                }
                foreach (var rel in json)
                {
                    if (rel!["draft"]!.GetValue<bool>() || (rel["prerelease"]!.GetValue<bool>() && !src.Prerelease)) continue;
                    var assets = rel["assets"]!.AsArray()
                        .Select(a => (Name: a!["name"]!.GetValue<string>(), Url: a["browser_download_url"]!.GetValue<string>()))
                        .Where(a => a.Name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) && rx.IsMatch(a.Name)).ToList();
                    var best = PickForAbi(assets, abis, a => a.Name);
                    if (best != default) return new Resolved(best.Url, best.Name, rel["tag_name"]!.GetValue<string>());
                }
                throw new InvalidOperationException($"No compatible APK found in the latest releases of {src.Repo}");
            }
            case "index":
            {
                // Official download page(s), space-separated (e.g. one directory per ABI): newest stable .apk link matching the pattern, best ABI.
                var rx = new Regex(src.Asset ?? @"\.apk$", RegexOptions.IgnoreCase);
                var found = new List<Uri>();
                foreach (var p in src.Url!.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var page = new Uri(p);
                    var html = await Http.GetStringAsync(page, ct);
                    found.AddRange(Regex.Matches(html, @"href=""([^""]+\.apk)""", RegexOptions.IgnoreCase)
                        .Select(m => new Uri(page, System.Net.WebUtility.HtmlDecode(m.Groups[1].Value))));
                }
                var links = found
                    .Where(u => u.Scheme == "https")
                    .Select(u => (Name: Path.GetFileName(u.LocalPath), Url: u.ToString())).DistinctBy(a => a.Url)
                    .Where(a => rx.IsMatch(a.Name) && (src.Prerelease || !Regex.IsMatch(a.Name, @"beta|alpha|[-_.]rc\d", RegexOptions.IgnoreCase)))
                    .ToList();
                static Version Ver(string n) => Regex.Match(n, @"\d+(\.\d+){1,3}") is { Success: true } m && Version.TryParse(m.Value, out var v) ? v : new Version(0, 0);
                var newest = links.Select(a => Ver(a.Name)).DefaultIfEmpty(new Version(0, 0)).Max()!;
                var pick = PickForAbi(links.Where(a => Ver(a.Name) == newest).ToList(), abis, a => a.Name);
                return pick != default ? new Resolved(pick.Url, pick.Name, newest.ToString()) : throw new InvalidOperationException($"No compatible APK found on {new Uri(src.Url.Split(' ')[0]).Host}");
            }
            case "fdroid":
            {
                // The API's "suggested" build can be any ABI (VLC's is x86_64), so pick by ABI from the package page,
                // which lists each build's versionCode and native code. Builds without native code suit every TV.
                var json = JsonNode.Parse(await Http.GetStringAsync($"https://f-droid.org/api/v1/packages/{src.Id}", ct))!;
                var suggested = json["suggestedVersionCode"]!.GetValue<long>();
                var versions = json["packages"]!.AsArray().Select(p => (Code: p!["versionCode"]!.GetValue<long>(), Name: p["versionName"]!.GetValue<string>()))
                    .Where(p => p.Code <= suggested).ToList();
                var newest = versions.First(v => v.Code == suggested).Name;
                var html = await Http.GetStringAsync($"https://f-droid.org/en/packages/{src.Id}/", ct);
                var native = Regex.Matches(html, @"<li class=""package-version""[\s\S]*?<a name=""(\d+)""></a>([\s\S]*?)</li>")
                    .DistinctBy(m => m.Groups[1].Value)
                    .ToDictionary(m => long.Parse(m.Groups[1].Value), m => Regex.Matches(m.Groups[2].Value, @"package-nativecode"">([^<]+)<").Select(n => n.Groups[1].Value.Trim()).ToList());
                var builds = versions.Where(v => v.Name == newest).Select(v => (v.Code, Abis: native.GetValueOrDefault(v.Code, []))).ToList();
                var fit = abis.Select(a => builds.FindIndex(b => b.Abis.Contains(a))).FirstOrDefault(i => i >= 0, -1);
                if (fit < 0) fit = builds.FindIndex(b => b.Abis.Count == 0);
                if (fit < 0) throw new InvalidOperationException($"F-Droid has no {newest} build of {app.Name} for {string.Join(", ", abis)}");
                var vc = builds[fit].Code;
                return new Resolved($"https://f-droid.org/repo/{src.Id}_{vc}.apk", $"{src.Id}_{vc}.apk", newest);
            }
            case "url":
                return new Resolved(src.Url!, Path.GetFileName(new Uri(src.Url!).LocalPath) is { Length: > 0 } f && f.Contains('.') ? f : app.Id + ".apk", "latest");
            default:
                throw new InvalidOperationException(src.Note ?? $"{app.Name} can't be sideloaded — get it from the TV's app store.");
        }
    }

    /// <summary>Rank assets: exact device ABI first (in the device's preference order), then universal, never x86 on ARM.</summary>
    static T PickForAbi<T>(List<T> assets, IReadOnlyList<string> abis, Func<T, string> name)
    {
        static string[] Tags(string abi) => abi switch
        {
            "arm64-v8a" => ["arm64-v8a", "arm64", "aarch64", "armv8"],
            "armeabi-v7a" => ["armeabi-v7a", "armeabi", "armv7", "arm32", "-arm."],
            "x86_64" => ["x86_64", "x64"],
            "x86" => ["x86"],
            _ => [abi],
        };
        var allTags = new[] { "arm64-v8a", "arm64", "aarch64", "armv8", "armeabi-v7a", "armeabi", "armv7", "arm32", "-arm.", "x86_64", "x64", "x86" };
        // Index-based: FirstOrDefault on value tuples yields a non-null default, which would mask "not found".
        bool Has(T a, string[] tags) => tags.Any(t => name(a).Contains(t, StringComparison.OrdinalIgnoreCase));
        foreach (var abi in abis)
        {
            var i = assets.FindIndex(a => Has(a, Tags(abi)));
            if (i >= 0) return assets[i];
        }
        // No ABI in the name = universal build
        var u = assets.FindIndex(a => !Has(a, allTags));
        return u >= 0 ? assets[u] : abis.Count == 0 && assets.Count > 0 ? assets[0] : default!;
    }

    public static async Task<string> DownloadAsync(Resolved r, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var safe = Regex.Replace(r.FileName, @"[^\w.\-]", "_");
        var dest = Path.Combine(CacheDir, safe);
        using var resp = await Http.GetAsync(r.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;
        var tmp = dest + ".part";
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var fs = File.Create(tmp))
        {
            var buf = new byte[1 << 16];
            long done = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }
        // An APK is a zip: reject HTML error pages and other junk before handing it to adb.
        await using (var check = File.OpenRead(tmp))
            if (check.ReadByte() != 'P' || check.ReadByte() != 'K')
            {
                check.Close(); File.Delete(tmp);
                throw new InvalidOperationException($"The download from {new Uri(r.Url).Host} wasn't an APK file");
            }
        File.Move(tmp, dest, true);
        return dest;
    }

    /// <summary>Resolve, download, install, then run the app's post-install commands.</summary>
    public static async Task<string> InstallAsync(string serial, CatalogApp app, IProgress<string> status, CancellationToken ct = default)
    {
        status.Report($"Finding the latest {app.Name}…");
        var r = await ResolveAsync(app, (await Devices.GetAsync(serial)).Abis, ct);
        status.Report($"Downloading {app.Name} {r.Version}…");
        var file = await DownloadAsync(r, new Progress<double>(p => status.Report($"Downloading {app.Name} {r.Version}… {p:P0}")), ct);
        try
        {
            status.Report($"Installing {app.Name} {r.Version}… if the TV shows a prompt, answer it with the remote.");
            await Tv.InstallAsync(serial, file);
            foreach (var cmd in app.After) await Adb.ShellAsync(serial, cmd);
        }
        finally { File.Delete(file); }
        return $"{app.Name} {r.Version} installed";
    }

    // ---------- recipes ----------

    public static IEnumerable<(string Path, Recipe Recipe)> Recipes()
    {
        foreach (var f in Directory.EnumerateFiles(RecipesDir, "*.json").Order())
        {
            Recipe? r = null;
            try { r = JsonSerializer.Deserialize(File.ReadAllText(f), CatalogJson.Default.Recipe); } catch (JsonException) { }
            if (r is not null) yield return (f, r);
        }
    }

    public static void ValidateRecipe(Recipe r)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) throw new InvalidOperationException("Give the recipe a name");
        if (r.Steps.Count == 0) throw new InvalidOperationException("The recipe has no steps");
        foreach (var s in r.Steps)
        {
            var set = new object?[] { s.Install, s.App, s.Shell, s.Launcher, s.Launch, s.Tweak }.Count(x => x is not null);
            if (set != 1) throw new InvalidOperationException("Each step must do exactly one thing");
            if (s.Install is not null && Find(s.Install) is null) throw new InvalidOperationException($"Unknown catalog app '{s.Install}'");
            if (s.App is not null) Validate(s.App);
            if (s.Launcher is not null) Adb.RequirePackage(s.Launcher);
            if (s.Launch is not null) Adb.RequirePackage(s.Launch);
            if (s.Value is not null && s.Tweak is null) throw new InvalidOperationException("Only tweak steps take a value");
            if (s.Tweak is not null)
            {
                var t = Tweaks.Find(s.Tweak) ?? throw new InvalidOperationException($"Unknown tweak '{s.Tweak}'");
                if (t.Choices is not null ? s.Value is null || !t.Choices.ContainsValue(s.Value) : s.Value is not null)
                    throw new InvalidOperationException($"Tweak '{s.Tweak}' needs {(t.Choices is not null ? "one of its option values" : "no value")}");
            }
        }
    }

    public static string SaveRecipe(Recipe r)
    {
        ValidateRecipe(r);
        var path = Path.Combine(RecipesDir, Regex.Replace(r.Name, @"[^\w\- ]", "").Trim() + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(r, CatalogJson.Default.Recipe));
        return path;
    }

    /// <summary>Share code: "tvdeck:" + base64url(gzip(json)). Small enough to paste in chat.</summary>
    public static string ShareCode(Recipe r)
    {
        using var ms = new MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.SmallestSize))
            JsonSerializer.Serialize(gz, r, CatalogJson.Default.Recipe);
        return "tvdeck:" + Convert.ToBase64String(ms.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static Recipe ParseShared(string text)
    {
        text = text.Trim();
        Recipe? r;
        if (text.StartsWith('{')) r = JsonSerializer.Deserialize(text, CatalogJson.Default.Recipe);
        else
        {
            if (!text.StartsWith("tvdeck:")) throw new InvalidOperationException("Paste a recipe code (starts with tvdeck:) or recipe JSON");
            var b64 = text[7..].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            using var gz = new System.IO.Compression.GZipStream(new MemoryStream(Convert.FromBase64String(b64)), System.IO.Compression.CompressionMode.Decompress);
            using var limited = new MemoryStream();
            gz.CopyTo(limited, 1 << 16);
            if (limited.Length > 1 << 20) throw new InvalidOperationException("Recipe is too large");
            limited.Position = 0;
            r = JsonSerializer.Deserialize(limited, CatalogJson.Default.Recipe);
        }
        if (r is null) throw new InvalidOperationException("That recipe is empty");
        ValidateRecipe(r);
        return r;
    }

    /// <summary>Runs every step; returns notes about steps that ran but didn't take effect on this TV.</summary>
    public static async Task<List<string>> RunRecipeAsync(string serial, Recipe r, IProgress<string> status, CancellationToken ct = default)
    {
        var notes = new List<string>();
        ValidateRecipe(r);
        await Snapshots.EnsureAsync(serial);
        Platform? platform = null;
        HashSet<string>? installed = null;
        for (var i = 0; i < r.Steps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var s = r.Steps[i];
            var prefix = $"Step {i + 1} of {r.Steps.Count}: ";
            var inner = new Progress<string>(m => status.Report(prefix + m));
            if (s.Install is not null) await InstallAsync(serial, Find(s.Install)!, inner, ct);
            else if (s.App is not null) await InstallAsync(serial, s.App, inner, ct);
            else if (s.Shell is not null) { status.Report(prefix + s.Shell); await Adb.ShellAsync(serial, s.Shell, 120, ct); }
            else if (s.Launcher is not null)
            {
                status.Report(prefix + "setting launcher");
                if (!await Tv.SetLauncherAsync(serial, s.Launcher)) notes.Add($"{s.Launcher}: {Tv.StockHomeNote}");
            }
            else if (s.Launch is not null) { status.Report(prefix + "opening " + s.Launch); await Tv.LaunchAsync(serial, s.Launch); }
            else if (s.Tweak is not null)
            {
                var t = Tweaks.Find(s.Tweak)!;
                platform ??= (await Devices.GetAsync(serial)).Platform;
                // Re-read installed apps: earlier steps may have just installed what this tweak needs.
                if (t.Requires is not null || installed is null) installed = await Tweaks.InstalledAsync(serial);
                if (!t.Applies(platform.Value, installed)) { status.Report(prefix + $"{t.Title} — skipped, not for this TV"); continue; }
                status.Report(prefix + s.Describe(Find));
                await Tweaks.SetAsync(serial, t, true, s.Value);
            }
        }
        return notes;
    }

    /// <summary>The recipes that ship with TVDeck (Assets/recipes.json).</summary>
    public static List<Recipe> BuiltInRecipes()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "Assets", "recipes.json");
        return File.Exists(file) ? JsonSerializer.Deserialize(File.ReadAllText(file), CatalogJson.Default.ListRecipe) ?? [] : [];
    }

    /// <summary>Built-in plus the user's saved recipes.</summary>
    public static IEnumerable<Recipe> AllRecipes() => BuiltInRecipes().Concat(Recipes().Select(r => r.Recipe));
}
