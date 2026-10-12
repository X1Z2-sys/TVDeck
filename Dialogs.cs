using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TVDeck;

public static class Dialogs
{
    public static async Task<bool> Confirm(XamlRoot root, string title, string body, string primary)
    {
        var d = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await d.ShowAsync() == ContentDialogResult.Primary;
    }

    public static async Task<string?> Prompt(XamlRoot root, string title, string header, string primary, string initial = "", bool multiline = false)
    {
        var box = new TextBox { Header = header, Text = initial, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multiline ? 160 : 0, MinWidth = 420 };
        var d = new ContentDialog
        {
            XamlRoot = root, Title = title, Content = box,
            PrimaryButtonText = primary, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };
        return await d.ShowAsync() == ContentDialogResult.Primary ? box.Text : null;
    }

    public static Task Show(XamlRoot root, string title, string body)
    {
        var d = new ContentDialog
        {
            XamlRoot = root, Title = title, CloseButtonText = "Close",
            Content = new ScrollViewer
            {
                MaxHeight = 480,
                Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas") },
            },
        };
        return d.ShowAsync().AsTask();
    }

    /// <summary>Native file open picker (unpackaged-safe via window handle).</summary>
    public static async Task<IReadOnlyList<string>> PickFiles(params string[] extensions)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance));
        foreach (var x in extensions.DefaultIfEmpty("*")) picker.FileTypeFilter.Add(x);
        var files = await picker.PickMultipleFilesAsync();
        return files.Select(f => f.Path).ToList();
    }

    public static async Task<string?> PickFolder()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance));
        picker.FileTypeFilter.Add("*");
        return (await picker.PickSingleFolderAsync())?.Path;
    }
}
