using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using TVDeck.Core;

namespace TVDeck.Pages;

public sealed partial class AppsPage : Page
{
    List<AppItem> _all = [];
    bool _includesSystem;

    public AppsPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Session.DeviceChanged += Reload;
        Reload();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => Session.DeviceChanged -= Reload;

    string Mode => (Filter.SelectedItem?.Tag as string) ?? "mine";

    async void Reload()
    {
        var s = Session.Serial;
        List.ItemsSource = null;
        State.Visibility = Visibility.Visible;
        Ring.IsActive = s is not null;
        StateText.Text = s is null ? "Connect a TV to see its apps." : "Reading apps from the TV…";
        if (s is null) return;
        _includesSystem = Mode is "all" or "disabled";
        var ok = await Session.Try(async () => _all = (await Tv.AppsAsync(s, _includesSystem)).Select(r => new AppItem(r)).ToList(), "Couldn't read apps");
        Ring.IsActive = false;
        if (!ok) { StateText.Text = "Couldn't read the app list. Check the connection and press Reload."; return; }
        Apply();
    }

    void Apply()
    {
        var q = Search.Text.Trim();
        var rows = _all.Where(a => Mode switch
        {
            "store" => a.Row.FromStore,
            "side" => !a.Row.FromStore && !a.Row.System,
            "disabled" => a.Row.Disabled,
            "mine" => !a.Row.System,
            _ => true,
        } && (q.Length == 0 || a.Package.Contains(q, StringComparison.OrdinalIgnoreCase) || a.Name.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        List.ItemsSource = rows;
        Count.Text = $"{rows.Count} apps";
        State.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StateText.Text = Mode == "disabled" ? "Nothing is disabled." : "No apps match.";
    }

    void Filter_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (Mode is "all" or "disabled" && !_includesSystem) Reload(); else Apply();
    }

    void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => Apply();
    void Reload_Click(object sender, RoutedEventArgs e) => Reload();

    async void Open_Click(object sender, RoutedEventArgs e)
    {
        var a = (AppItem)((FrameworkElement)sender).Tag;
        await Session.Try(() => Tv.LaunchAsync(Session.Require(), a.Package), $"Couldn't open {a.Name}");
    }

    void More_Click(object sender, RoutedEventArgs e)
    {
        var el = (FrameworkElement)sender;
        var a = (AppItem)el.Tag;
        var menu = new MenuFlyout();
        void Add(string text, string glyph, Func<Task> act)
        {
            var i = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            i.Click += async (_, _) => await act();
            menu.Items.Add(i);
        }
        Add("App info", "\uE946", async () => await Session.Try(async () => await Dialogs.Show(XamlRoot, a.Name, $"{a.Package}\n\n{await Tv.AppDetailAsync(Session.Require(), a.Package)}")));
        Add("Force stop", "\uE71A", () => Do(a, "stop", $"{a.Name} stopped"));
        Add("Make home screen", "\uE80F", () => Session.Try(async () =>
        {
            if (await Tv.SetLauncherAsync(Session.Require(), a.Package)) Session.Notify($"{a.Name} is now the home screen.");
            else Session.Notify($"{a.Name}: {Tv.StockHomeNote}", "Home button unchanged", InfoBarSeverity.Warning);
        }, "Couldn't set launcher"));
        Add("Save APK to PC…", "\uE896", () => SaveApk(a));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Clear data…", "\uE894", async () =>
        {
            if (await Dialogs.Confirm(XamlRoot, $"Clear {a.Name}'s data?", "This signs you out and deletes the app's settings and downloads on the TV. It can't be undone.", "Clear data"))
                await Do(a, "clear", $"{a.Name}'s data cleared");
        });
        if (a.Row.Disabled) Add("Enable", "\uE73E", () => Do(a, "enable", $"{a.Name} enabled", reload: true));
        else Add("Disable…", "\uE8D8", async () =>
        {
            if (await Dialogs.Confirm(XamlRoot, $"Disable {a.Name}?", "It disappears from the TV until you enable it again here. Disabling core system apps can break the home screen.", "Disable"))
                await Do(a, "disable", $"{a.Name} disabled", reload: true);
        });
        Add("Uninstall…", "\uE74D", async () =>
        {
            var body = a.Row.System
                ? "This removes the app for your profile only. You can bring it back with Restore from the Everything list."
                : "This removes the app and its data from the TV.";
            if (await Dialogs.Confirm(XamlRoot, $"Uninstall {a.Name}?", body, "Uninstall"))
                await Do(a, "uninstall", $"{a.Name} uninstalled", reload: true);
        });
        if (a.Row.System) Add("Restore (if removed)", "\uE777", () => Do(a, "restore", $"{a.Name} restored", reload: true));
        menu.ShowAt(el);
    }

    async Task Do(AppItem a, string action, string done, bool reload = false)
    {
        if (await Session.Try(async () => { await Tv.AppActionAsync(Session.Require(), a.Package, action); Session.Notify(done); }, $"Couldn't {action} {a.Name}") && reload)
            Reload();
    }

    async Task SaveApk(AppItem a)
    {
        var folder = await Dialogs.PickFolder();
        if (folder is null) return;
        await Session.Try(async () =>
        {
            var dest = await Tv.BackupAsync(Session.Require(), [a.Package], folder);
            Session.Notify($"Saved to {Path.Combine(dest, a.Package)}", "APK saved");
        }, "Couldn't save the APK");
    }
}
