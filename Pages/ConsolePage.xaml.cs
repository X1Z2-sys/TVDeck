using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TVDeck.Core;

namespace TVDeck.Pages;

public sealed partial class ConsolePage : Page
{
    static readonly List<string> History = [];
    static string _log = "";
    int _cursor;

    public ConsolePage()
    {
        InitializeComponent();
        Output.Text = _log;
        _cursor = History.Count;
    }

    void Preset_Click(object sender, RoutedEventArgs e) => _ = Run((string)((FrameworkElement)sender).Tag);
    void Run_Click(object sender, RoutedEventArgs e) => _ = Run(Input.Text);
    void Clear_Click(object sender, RoutedEventArgs e) => Output.Text = _log = "";

    void Input_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Enter: _ = Run(Input.Text); e.Handled = true; break;
            case Windows.System.VirtualKey.Up when History.Count > 0:
                _cursor = Math.Max(0, _cursor - 1); Input.Text = History[_cursor]; Input.SelectionStart = Input.Text.Length; e.Handled = true; break;
            case Windows.System.VirtualKey.Down when History.Count > 0:
                _cursor = Math.Min(History.Count, _cursor + 1); Input.Text = _cursor < History.Count ? History[_cursor] : ""; e.Handled = true; break;
        }
    }

    async Task Run(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.Length == 0) return;
        if (History.LastOrDefault() != cmd) History.Add(cmd);
        _cursor = History.Count;
        Input.Text = "";
        string result;
        try { result = await Adb.ShellAsync(Session.Require(), cmd, timeoutSec: 120); }
        catch (AdbException ex) { result = "⚠ " + ex.Message; }
        _log += $"$ {cmd}\n{result.TrimEnd()}\n\n";
        if (_log.Length > 200_000) _log = _log[^150_000..];
        Output.Text = _log;
        Scroll.UpdateLayout();
        Scroll.ChangeView(null, Scroll.ScrollableHeight, null, disableAnimation: true);
    }
}
