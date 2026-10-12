using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using TVDeck.Core;

namespace TVDeck.Pages;

public sealed partial class BackupPage : Page
{
    static string Root => Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TVDeck", "Backups")).FullName;

    public BackupPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e) { ListBackups(); ListSnapshots(); }

    void ListBackups() =>
        Backups.ItemsSource = Directory.GetDirectories(Root).Select(Path.GetFileName).OrderDescending().ToList();

    void ListSnapshots() =>
        Snaps.ItemsSource = Snapshots.List().Select(Path.GetFileNameWithoutExtension).ToList();

    async void Snap_Click(object sender, RoutedEventArgs e)
    {
        if (Session.Serial is not { } s) { Session.Notify("Connect a TV first.", "Snapshot", InfoBarSeverity.Informational); return; }
        SnapBtn.IsEnabled = false;
        await Session.Try(async () =>
        {
            var path = await Snapshots.SaveAsync(s, "manual");
            ListSnapshots();
            Snaps.SelectedItem = Path.GetFileNameWithoutExtension(path);
            Session.Notify("Snapshot saved.", "Snapshot");
        }, "Couldn't take a snapshot");
        SnapBtn.IsEnabled = true;
    }

    async void Rollback_Click(object sender, RoutedEventArgs e)
    {
        if (Snaps.SelectedItem is not string name) { Session.Notify("Pick a snapshot first.", "Restore", InfoBarSeverity.Informational); return; }
        if (Session.Serial is not { } s) { Session.Notify("Connect a TV first.", "Restore", InfoBarSeverity.Informational); return; }
        RollbackBtn.IsEnabled = false;
        await Session.Try(async () =>
        {
            var snap = Snapshots.Load(Path.Combine(Snapshots.Dir, name + ".json"));
            var serialNo = (await Adb.ShellAsync(s, "getprop ro.serialno")).Trim();
            var otherTv = snap.SerialNo.Length > 0 && snap.SerialNo != serialNo;
            var changes = await Snapshots.PlanAsync(s, snap);
            if (changes.Count == 0) { Session.Notify("This TV already matches that snapshot — nothing to change.", "Restore", InfoBarSeverity.Informational); return; }
            var shown = string.Join('\n', changes.Take(25).Select(c => "• " + c.What)) + (changes.Count > 25 ? $"\n…and {changes.Count - 25} more" : "");
            if (!await Dialogs.Confirm(XamlRoot, $"Restore {changes.Count} changes?",
                    (otherTv ? $"⚠ This snapshot is from another TV ({snap.Tv}).\n\n" : "") + shown, "Restore"))
                return;
            var failed = await Snapshots.RestoreAsync(s, changes);
            ListSnapshots();
            if (failed.Count == 0) Session.Notify($"{changes.Count} changes restored.", "Restored");
            else Session.Notify($"{changes.Count - failed.Count} restored. The TV refused:\n{string.Join('\n', failed)}", "Partly restored", InfoBarSeverity.Warning);
        }, "Couldn't restore the snapshot");
        RollbackBtn.IsEnabled = true;
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", Root) { UseShellExecute = true });

    void Busy(bool on, string title = "")
    {
        BackupBtn.IsEnabled = RestoreBtn.IsEnabled = !on;
        Bar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Progress.Severity = InfoBarSeverity.Informational;
        Progress.Title = title;
        Progress.Message = "";
        Progress.IsOpen = on || Progress.IsOpen;
    }

    void Done(InfoBarSeverity sev, string title, string msg)
    {
        Busy(false);
        Progress.Severity = sev;
        Progress.Title = title;
        Progress.Message = msg;
        Progress.IsOpen = true;
    }

    async void Backup_Click(object sender, RoutedEventArgs e)
    {
        string s;
        try { s = Session.Require(); } catch (AdbException ex) { Session.Error(ex, "No TV"); return; }
        Busy(true, "Backing up");
        try
        {
            var apps = (await Tv.AppsAsync(s, includeSystem: false)).Select(a => a.Package).ToList();
            var name = Tools.SafeFileName($"{await Tv.NameAsync(s)} {DateTime.Now:yyyy-MM-dd HHmm}");
            var dest = Path.Combine(Root, name);
            await Tv.BackupAsync(s, apps, dest, new Progress<string>(m => Progress.Message = m));
            Done(InfoBarSeverity.Success, "Backup finished", $"{apps.Count} apps saved to {dest}");
            ListBackups();
        }
        catch (Exception ex) when (ex is AdbException or IOException) { Done(InfoBarSeverity.Error, "Backup failed", ex.Message); }
    }

    async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (Backups.SelectedItem is not string name) { Session.Notify("Pick a backup first.", "Restore", InfoBarSeverity.Informational); return; }
        string s;
        try { s = Session.Require(); } catch (AdbException ex) { Session.Error(ex, "No TV"); return; }
        var folder = Path.Combine(Root, name);
        var count = Directory.GetDirectories(folder).Length;
        if (!await Dialogs.Confirm(XamlRoot, "Restore this backup?", $"{count} apps from \"{name}\" will be installed on the connected TV. Apps already on it get updated or kept.", "Restore")) return;
        Busy(true, "Restoring");
        try
        {
            var (ok, failed) = await Tv.RestoreAsync(s, folder, new Progress<string>(m => Progress.Message = m));
            if (failed.Count == 0) Done(InfoBarSeverity.Success, "Restore finished", $"{ok} apps installed.");
            else Done(InfoBarSeverity.Warning, "Restore finished with problems", $"{ok} installed. These failed:\n{string.Join('\n', failed)}");
        }
        catch (Exception ex) when (ex is AdbException or IOException) { Done(InfoBarSeverity.Error, "Restore failed", ex.Message); }
    }
}
