using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TVDeck.Core;
using TVDeck.Pages;
using Windows.Graphics;

namespace TVDeck;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hWnd);

    public static MainWindow Instance { get; private set; } = null!;

    static readonly Dictionary<string, Type> Pages = new()
    {
        ["home"] = typeof(HomePage),
        ["screen"] = typeof(ScreenPage),
        ["store"] = typeof(StorePage),
        ["recipes"] = typeof(RecipesPage),
        ["apps"] = typeof(AppsPage),
        ["tweaks"] = typeof(TweaksPage),
        ["debloat"] = typeof(DebloatPage),
        ["files"] = typeof(FilesPage),
        ["backup"] = typeof(BackupPage),
        ["console"] = typeof(ConsolePage),
    };

    readonly DispatcherTimer _toastTimer = new();
    bool _refreshing;

    public MainWindow()
    {
        InitializeComponent();
        Instance = this;
        Session.Ui = DispatcherQueue;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");
        ApplyTheme(SavedTheme);

        // Multi-pane shell: ~1240×800 DIPs, scaled to the monitor DPI.
        var scale = GetDpiForWindow(Win32Interop.GetWindowFromWindowId(AppWindow.Id)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1240 * scale), (int)(800 * scale)));

        _toastTimer.Tick += (_, _) => { Toast.IsOpen = false; _toastTimer.Stop(); };
        Session.Notified += ShowToast;
        Session.DevicesInvalidated += () => _ = RefreshDevicesAsync();
        Session.DeviceChanged += UpdateStatusDot;

        try { Log.Write("app", $"adb: {Adb.Path}"); } catch (Exception ex) { Log.Write("app", "adb missing", ex); }
        NavFrame.Navigate(typeof(HomePage));
        _ = RefreshDevicesAsync();
    }

    void ShowToast(string title, string message, InfoBarSeverity severity)
    {
        Toast.Title = title;
        Toast.Message = message;
        Toast.Severity = severity;
        Toast.IsOpen = true;
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(severity == InfoBarSeverity.Error ? 12 : 5);
        _toastTimer.Start();
    }

    async Task RefreshDevicesAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var keep = Session.Serial;
            List<DeviceRow> rows = [];
            await Session.Try(async () => rows = await Adb.DevicesAsync(), "Couldn't list devices");
            Log.Write("session", $"refresh: {rows.Count} devices");
            Session.Rows = rows;
            DevicePicker.ItemsSource = rows;
            var sel = rows.FirstOrDefault(r => r.Serial == keep);
            if (keep is not null && sel is null)
            {
                // The selected TV vanished (or lost authorization/offline): drop it, never pick another one silently.
                Session.Serial = null;
                Log.Write("session", $"selected device left the list: {keep}");
            }
            else Session.Serial = sel?.Serial ?? Session.Serial;
            DevicePicker.SelectedItem = sel;   // programmatic; SelectionChanged is a no-op for the same serial
            UpdateStatusDot();
        }
        finally { _refreshing = false; }
    }

    void UpdateStatusDot()
    {
        // Colors come from the styles; state text tells the user what adb actually sees.
        var (on, why) = Session.Serial switch
        {
            null => (false, "No TV selected"),
            { } s when Session.State == "device" => (true, $"Ready · {s}"),
            { } s when Session.State == "unauthorized" => (false, $"Accept the prompt on {s}"),
            { } s when Session.State == "offline" => (false, $"Offline · {s}"),
            { } s => (false, $"{Session.State} · {s}"),
        };
        StatusDot.Style = (Style)Application.Current.Resources[on ? "StatusDotOn" : "StatusDotOff"];
        ToolTipService.SetToolTip(StatusDot, why);
        DevicePicker.PlaceholderText = why;
    }

    void DevicePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DevicePicker.SelectedItem is DeviceRow d) Session.Serial = d.Serial;
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshDevicesAsync();

    void TitleBar_PaneToggleRequested(TitleBar sender, object args) => NavView.IsPaneOpen = !NavView.IsPaneOpen;

    void TitleBar_BackRequested(TitleBar sender, object args)
    {
        if (NavFrame.CanGoBack) NavFrame.GoBack();
    }

    /// <summary>System follows Windows (live); Oled is Dark on true black so OLED pixels switch off.</summary>
    public enum AppTheme { System, Light, Dark, Oled }

    public static AppTheme SavedTheme =>
        Windows.Storage.ApplicationData.Current.LocalSettings.Values["Theme"] is string s && Enum.TryParse<AppTheme>(s, out var t) ? t
        : AppTheme.System;   // also covers the old "Default" value

    public void ApplyTheme(AppTheme theme)
    {
        Windows.Storage.ApplicationData.Current.LocalSettings.Values["Theme"] = theme.ToString();
        var root = (Grid)Content;
        var oled = theme == AppTheme.Oled;
        // Mica (opaque, faint wallpaper tint) normally; OLED drops it for pure black.
        SystemBackdrop = oled ? null : new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };
        root.Background = oled ? new SolidColorBrush(Microsoft.UI.Colors.Black) : null;
        // NavigationView paints a grey layer behind pages; make it black too. ThemeResources re-resolve on a theme change,
        // so bounce the theme once to pick the override up.
        if (oled) NavView.Resources["NavigationViewContentBackground"] = new SolidColorBrush(Microsoft.UI.Colors.Black);
        else NavView.Resources.Remove("NavigationViewContentBackground");
        root.RequestedTheme = ElementTheme.Light;
        root.RequestedTheme = theme switch { AppTheme.Light => ElementTheme.Light, AppTheme.Dark or AppTheme.Oled => ElementTheme.Dark, _ => ElementTheme.Default };
        // Mica and every ThemeResource below the root follow this; the caption buttons are drawn by Windows and need telling.
        root.ActualThemeChanged -= CaptionButtonsFollowTheme;   // also fires when Windows switches while on "system"
        root.ActualThemeChanged += CaptionButtonsFollowTheme;
        CaptionButtonsFollowTheme(root, null);
    }

    /// <summary>
    /// With content extended into the title bar, Windows paints the caption glyphs from the app-level theme, so a forced
    /// Light on a dark system leaves white glyphs on a light bar. Colours mirror Fluent's text and subtle-fill tokens.
    /// </summary>
    void CaptionButtonsFollowTheme(FrameworkElement root, object? _)
    {
        var dark = root.ActualTheme == ElementTheme.Dark;
        Windows.UI.Color C(byte a, byte v) => Windows.UI.Color.FromArgb(a, v, v, v);
        var tb = AppWindow.TitleBar;
        tb.ButtonForegroundColor = tb.ButtonHoverForegroundColor = tb.ButtonPressedForegroundColor = dark ? C(255, 255) : C(0xE4, 0);
        tb.ButtonInactiveForegroundColor = dark ? C(0x5D, 255) : C(0x5C, 0);
        tb.ButtonHoverBackgroundColor = dark ? C(0x0F, 255) : C(0x09, 0);
        tb.ButtonPressedBackgroundColor = dark ? C(0x0A, 255) : C(0x06, 0);
        tb.ButtonBackgroundColor = tb.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
    }

    void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var page = args.IsSettingsSelected ? typeof(SettingsPage)
            : args.SelectedItem is NavigationViewItem { Tag: string tag } && Pages.TryGetValue(tag, out var p) ? p : null;
        if (page is not null && NavFrame.CurrentSourcePageType != page)
            NavFrame.Navigate(page, null, args.RecommendedNavigationTransitionInfo);
    }

    /// <summary>Lets pages jump to another section (e.g. Home → App store).</summary>
    public void Go(string tag) =>
        NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>().First(i => (string)i.Tag == tag);
}
