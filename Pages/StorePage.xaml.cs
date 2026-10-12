using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using TVDeck.Core;
using Windows.ApplicationModel.DataTransfer;

namespace TVDeck.Pages;

public sealed partial class StorePage : Page
{
    List<StoreItem> _items = [];

    public StorePage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Session.DeviceChanged += RefreshInstalled;
        Build();
        RefreshInstalled();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => Session.DeviceChanged -= RefreshInstalled;

    void Build()
    {
        _items = Catalog.Apps.Select(a => new StoreItem(a)).ToList();
        var selected = (Categories.SelectedItem as SelectorBarItem)?.Text ?? "All";
        Categories.Items.Clear();
        foreach (var c in _items.Select(i => i.Category).Distinct().Order().Prepend("All"))
            Categories.Items.Add(new SelectorBarItem { Text = c, IsSelected = c == selected });
        Filter();
    }

    void Filter()
    {
        var cat = (Categories.SelectedItem as SelectorBarItem)?.Text ?? "All";
        var q = Search.Text.Trim();
        var shown = _items.Where(i =>
            (cat == "All" || i.Category == cat) &&
            
            (_platform is not { } p || Devices.Applies(i.App.Platforms, p)) &&
            (q.Length == 0 || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || i.Description.Contains(q, StringComparison.OrdinalIgnoreCase) || i.App.Package.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Grid.ItemsSource = shown;
        Empty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    Platform? _platform;

    async void RefreshInstalled()
    {
        var s = Session.Serial;
        HashSet<string> pkgs = [];
        _platform = null;
        if (s is not null)
            await Session.Try(async () =>
            {
                _platform = (await Devices.GetAsync(s)).Platform;
                pkgs = (await Adb.ShellAsync(s, "pm list packages")).Split('\n').Select(l => l.Trim().Replace("package:", "")).ToHashSet();
            }, "Couldn't read installed apps");
        foreach (var i in _items) i.Installed = pkgs.Contains(i.App.Package);
        Filter();
    }

    void Categories_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => Filter();
    void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => Filter();
    void Filter_Changed(object sender, RoutedEventArgs e) => Filter();

    async void Install_Click(object sender, RoutedEventArgs e)
    {
        var item = (StoreItem)((FrameworkElement)sender).Tag;
        string serial;
        try { serial = Session.Require(); } catch (AdbException ex) { Session.Error(ex, "No TV"); return; }
        if (!item.CanInstall)
        {
            if (await Session.Try(() => Tv.OpenStorePageAsync(serial, item.App.Package), $"Couldn't open the store on the TV"))
                Session.Notify($"Opened {item.Name} in the TV's app store. Pick Get or Install with the remote.", item.Name, InfoBarSeverity.Success);
            return;
        }
        if (item.App.After.Count > 0 && !await Dialogs.Confirm(XamlRoot, $"Install {item.Name}?",
                $"After installing, TVDeck will also run these setup commands on the TV:\n\n{string.Join('\n', item.App.After)}", "Install"))
            return;

        item.Busy = true;
        try
        {
            var ok = await Session.Try(async () =>
            {
                var msg = await Catalog.InstallAsync(serial, item.App, new Progress<string>(m => item.Status = m.Replace($"{item.Name} ", "").Replace(item.Name, "")));
                item.Installed = true;
                item.Status = "Installed";
                Session.Notify(msg, "Done");
            }, $"Couldn't install {item.Name}");
            if (!ok) item.Status = "Failed";
        }
        finally { item.Busy = false; }
    }

    async void Open_Click(object sender, RoutedEventArgs e)
    {
        var item = (StoreItem)((FrameworkElement)sender).Tag;
        await Session.Try(() => Tv.LaunchAsync(Session.Require(), item.App.Package), $"Couldn't open {item.Name}");
    }

    async void InstallFile_Click(object sender, RoutedEventArgs e) =>
        await InstallFiles(await Dialogs.PickFiles(".apk", ".apks", ".xapk", ".apkm"));

    void Page_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Install on the TV";
        }
    }

    async void Page_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        await InstallFiles(items.OfType<Windows.Storage.StorageFile>().Select(f => f.Path).ToList());
    }

    async Task InstallFiles(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return;
        string serial;
        try { serial = Session.Require(); } catch (AdbException ex) { Session.Error(ex, "No TV"); return; }
        foreach (var f in files)
        {
            var name = Path.GetFileName(f);
            Session.Notify($"Installing {name}…", "Installing", InfoBarSeverity.Informational);
            if (await Session.Try(() => Tv.InstallAsync(serial, f), $"Couldn't install {name}"))
                Session.Notify($"{name} installed", "Done");
        }
        RefreshInstalled();
    }

    async void AddApp_Click(object sender, RoutedEventArgs e)
    {
        var app = await AppEditor.Show(XamlRoot);
        if (app is null) return;
        if (await Session.Try(() => { Catalog.AddUserApp(app); return Task.CompletedTask; }, "Couldn't add the app"))
        {
            Build();
            RefreshInstalled();
            Session.Notify($"{app.Name} was added to your store.", "Added");
        }
    }
}
