using Microsoft.UI.Dispatching;

namespace TVDeck.Core;

/// <summary>
/// App-wide state: the selected device and a status channel the shell renders.
/// The selected device is never silently switched: a vanished TV drops the selection and the shell shows it.
/// </summary>
public static class Session
{
    static string? _serial;
    public static DispatcherQueue Ui { get; set; } = null!;

    /// <summary>adb's own view: every entry, authorized or not.</summary>
    public static List<DeviceRow> Rows = [];

    /// <summary>adb's state word for the selected serial, "" when it isn't in the list.</summary>
    public static string State => Rows.FirstOrDefault(r => r.Serial == Serial)?.State ?? "";

    /// <summary>Ready only when adb reports "device". Unset when the TV vanished, went offline or lost authorization.</summary>
    public static string? Serial
    {
        get => _serial;
        set
        {
            if (_serial == value) return;
            _serial = value;
            Log.Write("session", value is null ? "device deselected" : $"device selected: {value}");
            DeviceChanged?.Invoke();
        }
    }

    public static string Require()
    {
        return State switch
        {
            "device" => Serial!,
            "unauthorized" => throw new AdbException("Accept the debugging prompt on the TV, then press Refresh devices."),
            "offline" => throw new AdbException("The TV shows offline. Restart it, then press Refresh devices."),
            "recovery" or "sideload" => throw new AdbException($"The TV is in {State} mode; only limited commands work."),
            _ => throw new AdbException("No TV selected. Connect one on the Home page."),
        };
    }

    public static event Action? DeviceChanged;
    public static event Action? DevicesInvalidated;
    public static void InvalidateDevices() => DevicesInvalidated?.Invoke();

    /// <summary>(title, message, severity) — shown as an InfoBar by MainWindow.</summary>
    public static event Action<string, string, Microsoft.UI.Xaml.Controls.InfoBarSeverity>? Notified;

    public static void Notify(string message, string title = "", Microsoft.UI.Xaml.Controls.InfoBarSeverity severity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success)
    {
        if (Ui is null) { Log.Write("session", $"{title}: {message}".TrimStart(':', ' ')); return; }
        Ui.TryEnqueue(() => Notified?.Invoke(title, message, severity));
    }

    public static void Error(Exception e, string title = "Something went wrong")
        => Notify(e.Message, title, Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);

    /// <summary>Run a UI action with uniform error reporting. Returns false if it failed.</summary>
    public static async Task<bool> Try(Func<Task> action, string errorTitle = "Something went wrong")
    {
        try { await action(); return true; }
        catch (Exception e) when (e is AdbException or IOException or HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Error(e, errorTitle);
            return false;
        }
    }
}
