using System.Text;

namespace TVDeck.Core;

/// <summary>Append-only text log in Documents\TVDeck\Logs. One line per event, timestamped; never logs command output.</summary>
public static class Log
{
    public static string Dir => Directory.CreateDirectory(Path.Combine(Tools.DataDir, "Logs")).FullName;

    static readonly object Gate = new();

    /// <summary>Safe anywhere, including before the window exists.</summary>
    public static void Write(string component, string message, Exception? ex = null)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{component}] {message}";
            if (ex is not null) line += " :: " + ex.Message;
            lock (Gate)
                File.AppendAllText(Path.Combine(Dir, $"tvdeck-{DateTime.Now:yyyyMMdd}.log"), line + "\n", Encoding.UTF8);
        }
        catch { /* logging must never break the app */ }
    }
}
