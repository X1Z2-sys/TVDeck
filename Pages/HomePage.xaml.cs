using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using TVDeck.Core;

namespace TVDeck.Pages;

public sealed partial class HomePage : Page
{
    public HomePage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Session.DeviceChanged += Load;
        Load();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => Session.DeviceChanged -= Load;

    async void Load()
    {
        var s = Session.Serial;
        foreach (var b in new Control[] { MirrorBtn, StoreBtn, PowerBtn }) b.IsEnabled = s is not null;
        if (s is null)
        {
            HeroName.Text = "No TV connected";
            HeroSub.Text = "Turn on network debugging on your TV, then connect below.";
            Facts.Visibility = Visibility.Collapsed;
            SetPhoto(Path.Combine(AppContext.BaseDirectory, "Assets", "Brands", "generic.png"));
            return;
        }
        HeroName.Text = "Connecting…";
        HeroSub.Text = s;
        await Session.Try(async () =>
        {
            var name = await Tv.NameAsync(s);
            var device = await Devices.GetAsync(s);
            SetPhoto(device.PhotoPath);
            var info = await Tv.InfoAsync(s);
            if (Session.Serial != s) return;   // switched devices meanwhile
            HeroName.Text = name;
            // Fire TV Edition TVs: the panel's real brand/model is more useful than the Amazon model code.
            HeroSub.Text = $"{(device.OemModel ?? info[0].Value)} · {info[1].Value} · {s}";
            Facts.ItemsSource = info.Skip(2).Select(i => new KeyValuePair<string, string>(i.Key, i.Value)).ToList();
            Facts.Visibility = Visibility.Visible;
        }, "Couldn't read the TV");
    }

    void SetPhoto(string path) => HeroPhoto.Source = path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
        ? new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(new Uri(path))
        : new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path));

    async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text;
        if (await Session.Try(async () => Session.Notify(await Adb.ConnectAsync(host), "Connected"), "Couldn't connect"))
            Session.InvalidateDevices();
    }

    void HostBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) Connect_Click(sender, e);
    }

    async void Pair_Click(object sender, RoutedEventArgs e)
    {
        if (await Session.Try(async () => Session.Notify(await Adb.PairAsync(PairHost.Text, PairCode.Text), "Paired"), "Pairing failed"))
            HostBox.Text = PairHost.Text.Split(':')[0];
    }

    async void Scan_Click(object sender, RoutedEventArgs e)
    {
        ScanRing.IsActive = true;
        ((Button)sender).IsEnabled = false;
        try
        {
            var lan = Adb.ScanLanAsync();
            List<string> mdns = [];
            try { mdns = (await Adb.MdnsAsync()).Where(m => m.Type.Contains("connect")).Select(m => m.Address).ToList(); } catch (AdbException) { }
            var all = (await lan).Concat(mdns).Distinct().ToList();
            Found.ItemsSource = all;
            if (all.Count == 0) Session.Notify("No TVs with network debugging were found. Check that the TV is on the same network and debugging is turned on.", "Nothing found", InfoBarSeverity.Warning);
        }
        finally
        {
            ScanRing.IsActive = false;
            ((Button)sender).IsEnabled = true;
        }
    }

    void Found_Click(object sender, RoutedEventArgs e)
    {
        HostBox.Text = (string)((FrameworkElement)sender).Tag;
        Connect_Click(sender, e);
    }

    void Mirror_Click(object sender, RoutedEventArgs e) => MainWindow.Instance.Go("screen");
    void Store_Click(object sender, RoutedEventArgs e) => MainWindow.Instance.Go("store");

    async void Sleep_Click(object sender, RoutedEventArgs e) => await Session.Try(() => Tv.KeyAsync(Session.Require(), "SLEEP"));
    async void Wake_Click(object sender, RoutedEventArgs e) => await Session.Try(() => Tv.KeyAsync(Session.Require(), "WAKEUP"));

    async void Reboot_Click(object sender, RoutedEventArgs e)
    {
        if (await Dialogs.Confirm(XamlRoot, "Restart the TV?", "The TV will restart and reconnect in about a minute.", "Restart"))
            await Session.Try(async () => { await Tv.RebootAsync(Session.Require()); Session.Notify("Restarting…"); });
    }

    async void Recovery_Click(object sender, RoutedEventArgs e)
    {
        if (await Dialogs.Confirm(XamlRoot, "Restart to recovery?", "Recovery mode is for advanced repair (factory reset, updates). You'll need the TV remote to get back out.", "Restart to recovery"))
            await Session.Try(() => Tv.RebootAsync(Session.Require(), "recovery"));
    }

    async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        var s = Session.Require();
        await Session.Try(() => Adb.RunAsync(["disconnect", s]));
        Session.InvalidateDevices();
    }
}
