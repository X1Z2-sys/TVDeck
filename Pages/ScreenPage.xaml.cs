using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using TVDeck.Core;

namespace TVDeck.Pages;

public sealed partial class ScreenPage : Page
{
    Mirror? _mirror;
    bool _webReady, _starting;
    bool _pressed;

    public ScreenPage()
    {
        InitializeComponent();
        QualityBox.ItemsSource = Mirror.Qualities.Select(q => q.Name).ToList();
        QualityBox.SelectedIndex = 0;
    }

    MirrorQuality Quality => Mirror.Qualities[Math.Max(0, QualityBox.SelectedIndex)];

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        Session.DeviceChanged += OnDeviceChanged;
        if (_webReady) return;
        try
        {
            await View.EnsureCoreWebView2Async();
            var core = View.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.SetVirtualHostNameToFolderMapping("tvdeck.local", Path.Combine(AppContext.BaseDirectory, "Assets"), CoreWebView2HostResourceAccessKind.DenyCors);
            core.WebMessageReceived += OnWebMessage;
            core.ContainsFullScreenElementChanged += (_, _) =>
                MainWindow.Instance.AppWindow.SetPresenter(core.ContainsFullScreenElement ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
            core.Navigate("https://tvdeck.local/screen.html");
        }
        catch (Exception ex)
        {
            IdleTitle.Text = "The built-in viewer couldn't start";
            IdleBody.Text = ex.Message + "\nUse Pop out to mirror in a scrcpy window instead.";
            IdleStart.Visibility = Visibility.Collapsed;
        }
    }

    protected override async void OnNavigatedFrom(NavigationEventArgs e)
    {
        Session.DeviceChanged -= OnDeviceChanged;
        await StopAsync();
    }

    async void OnDeviceChanged() => await StopAsync();

    void OnWebMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        using var doc = JsonDocument.Parse(args.WebMessageAsJson);
        var m = doc.RootElement;
        var t = m.GetProperty("t").GetString();
        switch (t)
        {
            case "ready":
                _webReady = true;
                if (!m.GetProperty("webcodecs").GetBoolean())
                {
                    IdleTitle.Text = "Built-in viewer not supported here";
                    IdleBody.Text = "Update the Microsoft Edge WebView2 runtime, or use Pop out.";
                }
                return;
            case "live":
                Idle.Visibility = Visibility.Collapsed;
                return;
            case "error":
                Session.Notify(m.GetProperty("m").GetString() ?? "", "Mirroring problem", InfoBarSeverity.Warning);
                return;
            case "fullscreen":
                Fullscreen_Click(this, new RoutedEventArgs());
                return;
        }
        if (_mirror is null) return;
        switch (t)
        {
            case "touch":
            {
                var a = m.GetProperty("a").GetInt32();
                _pressed = a != 1;
                _ = _mirror.Touch(a, m.GetProperty("x").GetInt32(), m.GetProperty("y").GetInt32(), _pressed);
                break;
            }
            case "scroll":
                _ = _mirror.Scroll(m.GetProperty("x").GetInt32(), m.GetProperty("y").GetInt32(), (float)m.GetProperty("h").GetDouble(), (float)m.GetProperty("v").GetDouble());
                break;
            case "back":
                _ = _mirror.BackOrScreenOn();
                break;
            case "key" when Keys.Code.TryGetValue(m.GetProperty("k").GetString() ?? "", out var code):
            {
                var a = m.GetProperty("a").GetInt32();
                var repeat = m.TryGetProperty("r", out var r) ? r.GetInt32() : 0;
                _ = a == 2 ? _mirror.Tap(code) : _mirror.Key(code, a, repeat);
                break;
            }
            case "text":
                _ = _mirror.Text(m.GetProperty("s").GetString() ?? "");
                break;
        }
    }

    async void Start_Click(object sender, RoutedEventArgs e) => await StartAsync();

    async Task StartAsync()
    {
        if (_starting || !_webReady) return;
        var serial = Session.Serial;
        if (serial is null) { Session.Notify("Connect a TV on the Home page first.", "No TV", InfoBarSeverity.Warning); return; }
        _starting = true;
        StartRing.IsActive = true;
        IdleStart.IsEnabled = StartBtn.IsEnabled = false;
        await StopAsync();
        try
        {
            await Session.Try(async () =>
            {
                var m = await Mirror.StartAsync(serial, Quality);
                var core = View.CoreWebView2;
                var ui = DispatcherQueue;
                // PostWebMessage must run on the UI thread; the reader thread queues each packet.
                m.Session += (w, h) => ui.TryEnqueue(() => core.PostWebMessageAsJson($$"""{"k":"s","w":{{w}},"h":{{h}}}"""));
                m.Packet += (data, config, key, pts) =>
                {
                    var json = config
                        ? $$"""{"k":"c","d":"{{Convert.ToBase64String(data)}}"}"""
                        : $$"""{"k":"f","key":{{(key ? "true" : "false")}},"pts":{{pts}},"d":"{{Convert.ToBase64String(data)}}"}""";
                    ui.TryEnqueue(() => { if (_mirror == m) core.PostWebMessageAsJson(json); });
                };
                m.Stopped += err => ui.TryEnqueue(async () =>
                {
                    if (_mirror != m) return;
                    await StopAsync();
                    if (err is not null) Session.Notify(err + " Press Start to reconnect.", "Mirroring stopped", InfoBarSeverity.Warning);
                });
                _mirror = m;
                StartBtn.Visibility = Visibility.Collapsed;
                StopBtn.Visibility = Visibility.Visible;
            }, "Couldn't start mirroring");
        }
        finally
        {
            _starting = false;
            StartRing.IsActive = false;
            IdleStart.IsEnabled = StartBtn.IsEnabled = true;
        }
    }

    async void Stop_Click(object sender, RoutedEventArgs e) => await StopAsync();

    async Task StopAsync()
    {
        var m = _mirror;
        _mirror = null;
        StartBtn.Visibility = Visibility.Visible;
        StopBtn.Visibility = Visibility.Collapsed;
        Idle.Visibility = Visibility.Visible;
        if (_webReady) View.CoreWebView2?.PostWebMessageAsJson("""{"k":"stop"}""");
        if (m is not null) await m.DisposeAsync();
    }

    async void Quality_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_mirror is not null) await StartAsync();   // restart with the new preset
    }

    void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (_webReady) _ = View.CoreWebView2.ExecuteScriptAsync("document.fullscreenElement ? document.exitFullscreen() : document.documentElement.requestFullscreen()");
    }

    void Stats_Click(object sender, RoutedEventArgs e)
    {
        if (_webReady) View.CoreWebView2.PostWebMessageAsJson($$"""{"k":"stats","on":{{(StatsToggle.IsChecked == true ? "true" : "false")}}}""");
    }

    async void PopOut_Click(object sender, RoutedEventArgs e)
    {
        var serial = Session.Serial;
        if (serial is null) { Session.Notify("Connect a TV first.", "No TV", InfoBarSeverity.Warning); return; }
        await StopAsync();   // one scrcpy session at a time
        await Session.Try(() => { Mirror.PopOut(serial, Quality, AudioToggle.IsChecked == true); return Task.CompletedTask; }, "Couldn't open scrcpy");
    }

    async void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        await Session.Try(async () =>
        {
            var png = await Tv.ScreenshotAsync(Session.Require());
            var dir = Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "TVDeck")).FullName;
            var file = Path.Combine(dir, $"TV {DateTime.Now:yyyy-MM-dd HHmmss}.png");
            await File.WriteAllBytesAsync(file, png);
            Session.Notify($"Saved to {file}", "Screenshot saved");
        }, "Screenshot failed");
    }

    async void Key_Click(object sender, RoutedEventArgs e)
    {
        var tag = (string)((FrameworkElement)sender).Tag;
        if (_mirror is not null && Keys.Code.TryGetValue(tag, out var code)) await _mirror.Tap(code);
        else await Session.Try(() => Tv.KeyAsync(Session.Require(), tag));
    }

    async void Send_Click(object sender, RoutedEventArgs e)
    {
        var text = TypeBox.Text;
        if (string.IsNullOrEmpty(text)) return;
        if (_mirror is not null) await _mirror.Text(text);
        else if (!await Session.Try(() => Tv.TextAsync(Session.Require(), text))) return;
        TypeBox.Text = "";
    }

    void TypeBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) Send_Click(sender, e);
    }
}
