using TVDeck.Core;
using Xunit;

namespace TVDeck.Tests;

// Safety-critical pure logic: what stands between user data and the TV's shell.
public class SafetyTests
{
    [Theory]
    [InlineData("com.spocky.projengmenu", true)]
    [InlineData("tv.samba.ssm", true)]
    [InlineData("com.example", true)]
    [InlineData("com.spocky.projengmenu; rm -rf /", false)]   // shell metacharacters
    [InlineData("$(id)", false)]
    [InlineData("`id`", false)]
    [InlineData("' ; id", false)]
    [InlineData("com..example", false)]
    [InlineData(".example", false)]
    [InlineData("", false)]
    [InlineData("com.example ", false)]
    public void Package_names_are_strict(string pkg, bool ok) =>
        Assert.Equal(ok, Adb.PackageName().IsMatch(pkg));

    [Theory]
    [InlineData("com.amazon.tv.acr'")]
    [InlineData("o'brien")]
    public void Quoting_survives_single_quotes(string s)
    {
        // Adb.Q's contract: the device shell must see exactly one argument == s.
        // POSIX: inside '...' everything is literal, and '\'' embeds a single quote.
        var quoted = Adb.Q(s);
        Assert.StartsWith("'", quoted);
        Assert.EndsWith("'", quoted);
        Assert.Equal(s, quoted[1..^1].Replace("'\\''", "'"));
    }

    [Fact]
    public void Quoting_neutralizes_injection()
    {
        var evil = "x'; settings put secure enabled_accessibility_services evil/app; #";
        var safe = Adb.Q(evil);
        Assert.StartsWith("'", safe);
        Assert.Equal(evil, safe[1..^1].Replace("'\\''", "'"));
    }

    [Theory]
    [InlineData("global/animator_duration_scale", true)]
    [InlineData("secure/limit_ad_tracking", true)]
    [InlineData("system/sound_effects_enabled", true)]
    [InlineData("global/abc'; id", false)]
    [InlineData("drop/table", false)]
    [InlineData("global/", false)]
    [InlineData("secure/limit_ad_tracking extra", false)]
    [InlineData("global/with space", false)]
    public void Snapshot_restore_only_touches_settings_keys(string key, bool ok) =>
        Assert.Equal(ok, Snapshots.SafeKey().IsMatch(key));

    [Fact]
    public void Shipped_json_holds_invariants()
    {
        var tweaks = Tweaks.All;
        Assert.NotEmpty(tweaks);
        Assert.All(tweaks, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Title));
            Assert.NotEmpty(t.Apply);
        });
        var debloaters = Debloaters.All;
        Assert.NotEmpty(debloaters);
        Assert.All(debloaters, d => Assert.Contains('/', d.Repo));
        // Every package in every list must be a strict package name - injection can't ride in via JSON.
        Assert.All(debloaters.SelectMany(d => d.Groups.Values.Concat(d.OptIn.Values).SelectMany(v => v)),
            p => Assert.Matches(Adb.PackageName(), p));
    }
}
