namespace TVDeck.Core;

// Test stub for the app's Session (the real one lives in the app project and binds WinUI types).
// Surface must stay identical to the members Core code uses: Rows, State, Serial, Require, events.
public static class Session
{
    public static List<DeviceRow> Rows = [];
    public static string State => Rows.FirstOrDefault(r => r.Serial == Serial)?.State ?? "";
    static string? _serial;
    public static string? Serial { get => _serial; set { if (_serial != value) { _serial = value; DeviceChanged?.Invoke(); } } }
    public static string Require() => State == "device" ? Serial! : throw new AdbException("No TV selected.");
    public static event Action? DeviceChanged;
    public static event Action? DevicesInvalidated;
    public static void InvalidateDevices() => DevicesInvalidated?.Invoke();
    public static void Notify(string m, string t = "", object? s = default) { }
    public static void Error(Exception e, string t = "Something went wrong") { }
}
