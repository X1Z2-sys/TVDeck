using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using TVDeck.Core;
using Windows.ApplicationModel.DataTransfer;

namespace TVDeck.Pages;

public sealed partial class FilesPage : Page
{
    string _path = "/sdcard/";

    public FilesPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Session.DeviceChanged += Reset;
        _ = Load(_path);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => Session.DeviceChanged -= Reset;

    void Reset() => _ = Load("/sdcard/");

    async Task Load(string path)
    {
        var s = Session.Serial;
        if (s is null) { List.ItemsSource = null; Empty.Text = "Connect a TV to browse its files."; Empty.Visibility = Visibility.Visible; return; }
        path = path.EndsWith('/') ? path : path + "/";
        await Session.Try(async () =>
        {
            var rows = await Tv.ListAsync(s, path);
            _path = path;
            List.ItemsSource = rows;
            Empty.Text = "This folder is empty.";
            Empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            var parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            Crumbs.ItemsSource = parts.Length == 0 ? ["/"] : parts.Prepend("/").ToList();
        }, "Couldn't open that folder");
    }

    string Full(FileRow f) => _path + f.Name;

    void Crumbs_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        var parts = ((List<string>)sender.ItemsSource).Skip(1).Take(args.Index);
        _ = Load("/" + string.Join('/', parts));
    }

    void Up_Click(object sender, RoutedEventArgs e)
    {
        var trimmed = _path.TrimEnd('/');
        _ = Load(trimmed.Length == 0 ? "/" : trimmed[..(trimmed.LastIndexOf('/') + 1)]);
    }

    void Reload_Click(object sender, RoutedEventArgs e) => _ = Load(_path);

    void List_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e) => OpenSelected();

    void List_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) OpenSelected();
        else if (e.Key == Windows.System.VirtualKey.Back) Up_Click(sender, e);
    }

    void Open_Click(object sender, RoutedEventArgs e) => OpenSelected();

    void OpenSelected()
    {
        if (List.SelectedItem is not FileRow f) return;
        if (f.IsDir) _ = Load(Full(f));
        else _ = Download(f);
    }

    void Download_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is FileRow f) _ = Download(f);
    }

    async Task Download(FileRow f)
    {
        var folder = await Dialogs.PickFolder();
        if (folder is null) return;
        await Session.Try(async () =>
        {
            await Tv.PullAsync(Session.Require(), Full(f), folder);
            Session.Notify($"Saved {f.Name} to {folder}", "Downloaded");
        }, $"Couldn't save {f.Name}");
    }

    async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not FileRow f) return;
        if (!await Dialogs.Confirm(XamlRoot, $"Delete {f.Name}?", f.IsDir ? "The folder and everything in it will be deleted from the TV." : "The file will be deleted from the TV.", "Delete")) return;
        if (await Session.Try(() => Tv.DeleteAsync(Session.Require(), Full(f)), "Couldn't delete")) await Load(_path);
    }

    async void Upload_Click(object sender, RoutedEventArgs e) => await Upload(await Dialogs.PickFiles());

    void Page_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = $"Send to {_path}";
    }

    async void Page_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        await Upload(items.Select(i => i.Path).ToList());
    }

    async Task Upload(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return;
        var dest = _path;
        string serial;
        try { serial = Session.Require(); } catch (AdbException ex) { Session.Error(ex, "No TV"); return; }
        foreach (var f in files)
        {
            Session.Notify($"Sending {Path.GetFileName(f)}…", "Sending", InfoBarSeverity.Informational);
            await Session.Try(() => Tv.PushAsync(serial, f, dest), $"Couldn't send {Path.GetFileName(f)}");
        }
        Session.Notify($"Sent {files.Count} item(s) to {dest}", "Done");
        await Load(dest);
    }
}
