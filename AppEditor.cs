using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TVDeck.Core;

namespace TVDeck;

/// <summary>Dialog for adding a custom app to the store (also used by the recipe builder).</summary>
public static class AppEditor
{
    public static async Task<CatalogApp?> Show(XamlRoot root)
    {
        var name = new TextBox { Header = "Name", PlaceholderText = "Jellyfin" };
        var pkg = new TextBox { Header = "Package name (optional, lets TVDeck show Installed/Open)", PlaceholderText = "org.jellyfin.androidtv" };
        var kind = new RadioButtons { Header = "Where to get it", SelectedIndex = 0, MaxColumns = 3, Items = { "GitHub releases", "F-Droid", "Direct link" } };
        var where = new TextBox { Header = "GitHub repository", PlaceholderText = "owner/repo or https://github.com/owner/repo" };
        var asset = new TextBox { Header = "Pick the APK whose name contains (optional)", PlaceholderText = "e.g. androidtv" };
        var category = new ComboBox { Header = "Category", ItemsSource = new[] { "Media servers", "Streaming", "Players", "Live TV & IPTV", "Launchers", "Screensavers", "Utilities", "Ad-blocking", "Game streaming", "Music" }, SelectedIndex = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
        var desc = new TextBox { Header = "Description (optional)" };
        kind.SelectionChanged += (_, _) =>
        {
            (where.Header, where.PlaceholderText) = kind.SelectedIndex switch
            {
                1 => ("F-Droid package id", "org.videolan.vlc"),
                2 => ("Download link (https)", "https://example.com/app.apk"),
                _ => ("GitHub repository", "owner/repo or https://github.com/owner/repo"),
            };
            asset.Visibility = kind.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        };

        var d = new ContentDialog
        {
            XamlRoot = root,
            Title = "Add an app to your store",
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = new ScrollViewer { Content = new StackPanel { Spacing = 12, MinWidth = 440, Children = { name, kind, where, asset, pkg, category, desc } } },
        };

        while (await d.ShowAsync() == ContentDialogResult.Primary)
        {
            var w = where.Text.Trim();
            var src = kind.SelectedIndex switch
            {
                1 => new AppSource { Type = "fdroid", Id = w },
                2 => new AppSource { Type = "url", Url = w },
                _ => new AppSource
                {
                    Type = "github",
                    Repo = Regex.Replace(w, @"^https?://github\.com/|/+$|\.git$", "").Split('/').Take(2) is var parts ? string.Join('/', parts) : w,
                    Asset = asset.Text.Trim() is { Length: > 0 } a ? Regex.Escape(a) : null,
                },
            };
            var app = new CatalogApp
            {
                Id = "user-" + Regex.Replace(name.Text.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-'),
                Name = name.Text.Trim(),
                Package = pkg.Text.Trim(),
                Category = (string)category.SelectedItem,
                Description = desc.Text.Trim(),
                Source = src,
            };
            try { Catalog.Validate(app); return app; }
            catch (Exception e) when (e is InvalidOperationException or AdbException or ArgumentException)
            {
                d.Title = e.Message;   // show the problem and let them fix it
            }
        }
        return null;
    }
}
