using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using TVDeck.Core;

namespace TVDeck.Pages;

/// <summary>Renders Assets/tweaks.json as grouped settings cards with live on/off state.</summary>
public sealed partial class TweaksPage : Page
{
    bool _loading;
    List<Tweak> _visible = [];
    Dictionary<string, string> _state = [];

    public TweaksPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Session.DeviceChanged += Load;
        Load();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => Session.DeviceChanged -= Load;

    static T Res<T>(string key) => (T)Application.Current.Resources[key];

    async void Load()
    {
        Groups.Children.Clear();
        var s = Session.Serial;
        NoDevice.Visibility = s is null ? Visibility.Visible : Visibility.Collapsed;
        RecommendPanel.Visibility = Visibility.Collapsed;
        Ring.IsActive = s is not null;
        Ring.Visibility = s is null ? Visibility.Collapsed : Visibility.Visible;
        if (s is null) return;

        _state = [];
        _visible = [];
        await Session.Try(async () =>
        {
            var platform = (await Devices.GetAsync(s)).Platform;
            var installed = await Tweaks.InstalledAsync(s);
            _visible = Tweaks.All.Where(t => t.Applies(platform, installed)).ToList();
            _state = await Tweaks.ReadAsync(s, _visible);
        }, "Couldn't read the TV's settings");
        Ring.Visibility = Visibility.Collapsed;

        var todo = PendingRecommended();
        RecommendPanel.Visibility = _visible.Any(t => t.Recommend is not null) ? Visibility.Visible : Visibility.Collapsed;
        RecommendBtn.IsEnabled = todo.Count > 0;
        RecommendBtn.Content = todo.Count > 0 ? $"Apply {todo.Count} recommended" : "All recommended tweaks are on";

        _loading = true;
        foreach (var group in _visible.GroupBy(t => t.Group))
        {
            Groups.Children.Add(new TextBlock { Text = group.Key, Style = Res<Style>("BodyStrongTextBlockStyle"), Margin = new Thickness(0, 20, 0, 4) });
            foreach (var t in group) Groups.Children.Add(Card(s, t, _state.GetValueOrDefault(t.Id)));
        }
        _loading = false;
    }

    List<Tweak> PendingRecommended() =>
        _visible.Where(t => t.Recommend is not null && !t.IsRecommendedState(_state.GetValueOrDefault(t.Id))).ToList();

    async void Recommend_Click(object sender, RoutedEventArgs e)
    {
        if (Session.Serial is not { } s) return;
        var todo = PendingRecommended();
        if (todo.Count == 0) return;
        var list = string.Join("\n", todo.Select(t => $"• {t.Title}" + (t.Choices is not null ? $": {t.Choices.First(kv => kv.Value == t.Recommend).Key}" : "")));
        if (!await Dialogs.Confirm(XamlRoot, $"Apply {todo.Count} recommended tweaks?",
                $"{list}\n\nEach one is low risk and can be undone on this page. TVDeck saves a settings snapshot first, so Backup → Settings snapshots can put everything back in one go.",
                "Apply"))
            return;
        RecommendBtn.IsEnabled = false;
        var failed = new List<string>();
        foreach (var t in todo)
        {
            try { await Tweaks.SetAsync(s, t, true, t.Choices is null ? null : t.Recommend); }
            catch (AdbException ex) { failed.Add($"{t.Title}: {ex.Message.Split('\n')[0]}"); }
        }
        if (failed.Count == 0) Session.Notify($"{todo.Count} recommended tweaks applied.", "Done");
        else Session.Notify($"{todo.Count - failed.Count} applied. The TV refused:\n{string.Join('\n', failed)}", "Partly applied", InfoBarSeverity.Warning);
        Load();
    }

    FrameworkElement Card(string serial, Tweak t, string? current)
    {
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleRow.Children.Add(new TextBlock { Text = t.Title, Style = Res<Style>("BodyTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center });
        if (t.Recommend is not null)
            titleRow.Children.Add(new Border
            {
                Style = Res<Style>("AccentBadge"), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "Recommended", Style = Res<Style>("AccentBadgeText") },
            });

        var desc = new TextBlock
        {
            Text = t.Description + (t.Risk.StartsWith("medium") || t.Risk.StartsWith("high") ? $"\n⚠ {t.Risk}" : ""),
            Style = Res<Style>("SecondaryCaption"),
        };
        var undo = new TextBlock { Text = t.UndoHint, Style = Res<Style>("SecondaryCaption") };
        var commands = new Expander
        {
            Header = "Commands", Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = new TextBlock
            {
                Text = "Apply:\n  adb shell " + string.Join("\n  adb shell ", t.Apply) +
                       (t.Undo.Count > 0 ? "\nUndo:\n  adb shell " + string.Join("\n  adb shell ", t.Undo) : ""),
                FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap,
            },
        };
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { titleRow, desc, undo, commands } };

        FrameworkElement control = t.Choices is { Count: > 0 } ? ChoiceControl(serial, t, current)
            : t.IsAction ? ActionControl(serial, t)
            : ToggleControl(serial, t, current);
        control.VerticalAlignment = VerticalAlignment.Top;

        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);

        return new Border { Style = Res<Style>("Card"), Child = grid };
    }

    FrameworkElement ChoiceControl(string serial, Tweak t, string? current)
    {
        var box = new ComboBox { MinWidth = 220, ItemsSource = t.Choices!.Keys.ToList() };
        A11yName(box, t.Title);
        var unset = string.IsNullOrEmpty(current) || current == "null";
        var match = t.Choices.FirstOrDefault(kv => kv.Value == "" ? unset : Tweak.SameValue(kv.Value, current)).Key;
        if (match is not null) box.SelectedItem = match;
        else box.PlaceholderText = unset ? "Default"
            // Millisecond timeouts read better as minutes ("840000" → "14 minutes").
            : t.Choices.Values.All(v => v.Length >= 5 && v.All(char.IsDigit)) && long.TryParse(current, out var ms) ? $"{ms / 60000.0:0.#} minutes"
            : current;
        box.SelectionChanged += async (_, _) =>
        {
            if (_loading || box.SelectedItem is not string label) return;
            await Run(serial, t, true, t.Choices[label], () => { _loading = true; box.SelectedItem = match; _loading = false; });
        };
        return box;
    }

    FrameworkElement ToggleControl(string serial, Tweak t, string? current)
    {
        var sw = new ToggleSwitch { IsOn = t.OnValue is not null && current == t.OnValue, OnContent = "On", OffContent = "Off", MinWidth = 0 };
        A11yName(sw, t.Title);
        sw.Toggled += async (_, _) =>
        {
            if (_loading) return;
            if (t.Risk.StartsWith("high") && sw.IsOn &&
                !await Dialogs.Confirm(XamlRoot, t.Title, $"{t.Description}\n\n{t.Risk}", "Turn on"))
            {
                _loading = true; sw.IsOn = false; _loading = false;
                return;
            }
            await Run(serial, t, sw.IsOn, null, () => { _loading = true; sw.IsOn = !sw.IsOn; _loading = false; });
        };
        return sw;
    }

    FrameworkElement ActionControl(string serial, Tweak t)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var run = new Button { Content = "Run", MinWidth = 80 };
        A11yName(run, $"Run: {t.Title}");
        run.Click += async (_, _) =>
        {
            run.IsEnabled = false;
            run.Content = "Running…";
            await Run(serial, t, true, null, () => { });
            run.Content = "Run";
            run.IsEnabled = true;
        };
        panel.Children.Add(run);
        if (t.Undo.Count > 0)
        {
            var undo = new Button { Content = "Undo", MinWidth = 80 };
            A11yName(undo, $"Undo: {t.Title}");
            undo.Click += async (_, _) => await Run(serial, t, false, null, () => { });
            panel.Children.Add(undo);
        }
        return panel;
    }

    static void A11yName(DependencyObject o, string name) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(o, name);

    async Task Run(string serial, Tweak t, bool on, string? choice, Action revert)
    {
        if (!await Session.Try(async () =>
            {
                await Tweaks.SetAsync(serial, t, on, choice);
                Session.Notify($"{t.Title}: {(t.IsAction ? (on ? "done" : "undone") : choice is not null ? "set" : on ? "on" : "off")}", "Applied");
            }, t.Title))
            revert();
    }
}
