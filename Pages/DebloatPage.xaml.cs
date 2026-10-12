using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using TVDeck.Core;

namespace TVDeck.Pages;

/// <summary>Curated GitHub debloat lists (Assets/debloaters.json), shown against what this TV actually has.</summary>
public sealed partial class DebloatPage : Page
{
    PackageState _state = new([], []);

    public DebloatPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Session.DeviceChanged += Load;
        Load();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => Session.DeviceChanged -= Load;

    static T Res<T>(string key) => (T)Application.Current.Resources[key];
    static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    async void Load()
    {
        List.Children.Clear();
        var s = Session.Serial;
        NoDevice.Visibility = s is null ? Visibility.Visible : Visibility.Collapsed;
        Ring.Visibility = s is null ? Visibility.Collapsed : Visibility.Visible;
        if (s is null) return;

        DeviceProfile? dev = null;
        await Session.Try(async () =>
        {
            dev = await Devices.GetAsync(s);
            _state = await Debloaters.ReadStateAsync(s);
        }, "Couldn't read the TV's apps");
        Ring.Visibility = Visibility.Collapsed;
        if (dev is null || Session.Serial != s) return;

        var lists = Debloaters.All.Where(d => Debloaters.Applies(d, dev.Platform, dev.Brand)).ToList();
        if (lists.Count == 0)
            List.Children.Add(new TextBlock { Text = $"No curated list matches this {dev.Brand} TV yet.", Style = Res<Style>("SecondaryCaption") });
        foreach (var d in lists) List.Children.Add(Card(s, d));
    }

    FrameworkElement Card(string serial, Debloater d)
    {
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = d.Name, Style = Res<Style>("SubtitleTextBlockStyle") },
                new TextBlock { Text = d.Desc, Style = Res<Style>("SecondaryCaption") },
                new HyperlinkButton { Content = $"github.com/{d.Repo}", NavigateUri = new Uri($"https://github.com/{d.Repo}"), Padding = new Thickness(0), FontSize = 12 },
            },
        });

        // Everything recommended that's on the TV and still enabled: one click for the common case.
        var pending = d.Groups.Values.SelectMany(p => p).Where(p => _state.Installed.Contains(p) && !_state.Disabled.Contains(p)).Distinct().ToList();
        if (pending.Count > 0)
        {
            var all = new Button { Content = $"Disable {Count(pending.Count, "recommended app")}", Style = Res<Style>("AccentButtonStyle"), VerticalAlignment = VerticalAlignment.Top };
            all.Click += async (_, _) => await Apply(serial, $"{d.Name}: recommended", pending, disable: true, all);
            header.Children.Add(Col(all, 1));
        }

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(header);
        if (d.Groups.Count + d.OptIn.Count == 0)
            panel.Children.Add(new TextBlock { Text = "This is a standalone tool rather than a package list — open it on GitHub to review and run it.", Style = Res<Style>("SecondaryCaption") });
        foreach (var g in d.Groups) panel.Children.Add(Group(serial, g.Key, g.Value, optional: false));
        foreach (var g in d.OptIn) panel.Children.Add(Group(serial, g.Key, g.Value, optional: true));
        foreach (var note in d.Notes ?? [])
            panel.Children.Add(new InfoBar { Severity = InfoBarSeverity.Warning, Message = note, IsOpen = true, IsClosable = false });

        return new Border { Style = Res<Style>("Card"), Padding = new Thickness(20, 16, 20, 16), Child = panel };
    }

    FrameworkElement Group(string serial, string label, string[] packages, bool optional)
    {
        var present = packages.Where(_state.Installed.Contains).ToList();
        var disabled = present.Where(_state.Disabled.Contains).ToList();
        var enabled = present.Except(disabled).ToList();

        var chip = new Border
        {
            Style = Res<Style>(optional ? "CautionChip" : "SuccessChip"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = optional ? "Optional" : "Recommended", Style = Res<Style>("ChipText") },
        };
        var status = present.Count == 0 ? "None of these are on this TV"
            : $"{present.Count} of {Count(packages.Length, "app")} on this TV" + (disabled.Count > 0 ? $" · {disabled.Count} disabled" : "");
        var title = new StackPanel
        {
            Spacing = 2, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = label, Style = Res<Style>("BodyStrongTextBlockStyle"), TextWrapping = TextWrapping.Wrap }, chip } },
                new TextBlock { Text = status, Style = Res<Style>("SecondaryCaption") },
            },
        };

        var disable = new Button { Content = enabled.Count > 0 ? $"Disable {enabled.Count}" : "Disable", MinWidth = 92, IsEnabled = enabled.Count > 0 };
        var restore = new Button { Content = disabled.Count > 0 ? $"Restore {disabled.Count}" : "Restore", MinWidth = 92, IsEnabled = disabled.Count > 0 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(disable, $"Disable {label}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(restore, $"Restore {label}");
        disable.Click += async (_, _) =>
        {
            if (optional && !await Dialogs.Confirm(XamlRoot, $"Disable {label}?", $"Optional groups turn a feature off. These {Count(enabled.Count, "app")} stop working until you press Restore:\n\n{string.Join('\n', enabled)}", "Disable"))
                return;
            await Apply(serial, label, enabled, disable: true, disable);
        };
        restore.Click += async (_, _) => await Apply(serial, label, disabled, disable: false, restore);

        var head = new Grid { ColumnSpacing = 8 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(title);
        head.Children.Add(Col(disable, 1));
        head.Children.Add(Col(restore, 2));

        // Package list: what each app is doing on this TV right now.
        var list = new StackPanel { Spacing = 4 };
        foreach (var p in packages)
        {
            var (glyph, state) = !_state.Installed.Contains(p) ? ("\uE711", "not on this TV") : _state.Disabled.Contains(p) ? ("\uE73E", "disabled") : ("\uE768", "running");
            var row = new Grid { ColumnSpacing = 10, Opacity = _state.Installed.Contains(p) ? 1 : 0.55 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12 });
            row.Children.Add(Col(new TextBlock { Text = p, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, IsTextSelectionEnabled = true }, 1));
            row.Children.Add(Col(new TextBlock { Text = state, Style = Res<Style>("SecondaryCaption") }, 2));
            list.Children.Add(row);
        }

        return new Expander
        {
            Header = head, Content = list,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
    }

    static FrameworkElement Col(FrameworkElement e, int column) { Grid.SetColumn(e, column); return e; }

    async Task Apply(string serial, string label, List<string> packages, bool disable, Button source)
    {
        source.IsEnabled = false;
        await Session.Try(async () =>
        {
            var refused = await Debloaters.SetAsync(serial, packages, disable);
            var done = packages.Count - refused.Count;
            var verb = disable ? "disabled" : "restored";
            if (refused.Count == 0) Session.Notify($"{label}: {Count(done, "app")} {verb}.", disable ? "Debloat applied" : "Restored");
            else Session.Notify($"{label}: {done} {verb}. The TV refused:\n{string.Join('\n', refused)}", "Partly done", InfoBarSeverity.Warning);
        }, label);
        Load();   // redraw from the TV's real state
    }
}
