using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;

namespace TVDeck.Core;

/// <summary>Minimal INotifyPropertyChanged base for list items.</summary>
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise(params string[] names)
    {
        foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}

public sealed class StoreItem(CatalogApp app) : Observable
{
    public CatalogApp App { get; } = app;
    public string Name => App.Name;
    public string Description => App.Description;
    public string Category => App.Category;
    public string SourceLabel => App.SourceLabel;
    public string Initial => App.Initial;
    public Visibility GrayVisibility => App.Gray ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Stable tile colour per category so the grid scans by type.</summary>
    public Microsoft.UI.Xaml.Media.SolidColorBrush Tile => new(Category switch
    {
        "Media servers" => Windows.UI.Color.FromArgb(255, 0x7B, 0x61, 0xFF),
        "Streaming" => Windows.UI.Color.FromArgb(255, 0xE8, 0x4A, 0x5F),
        "Players" => Windows.UI.Color.FromArgb(255, 0xF2, 0x8C, 0x28),
        "Live TV & IPTV" => Windows.UI.Color.FromArgb(255, 0x1F, 0xA2, 0x7A),
        "Launchers" => Windows.UI.Color.FromArgb(255, 0x2D, 0x7D, 0xD2),
        "Screensavers" => Windows.UI.Color.FromArgb(255, 0x45, 0x8F, 0xB8),
        "Ad-blocking" => Windows.UI.Color.FromArgb(255, 0x3A, 0xA6, 0x5B),
        "Game streaming" => Windows.UI.Color.FromArgb(255, 0x9B, 0x3D, 0xD6),
        "Music" => Windows.UI.Color.FromArgb(255, 0xD6, 0x3D, 0x8F),
        _ => Windows.UI.Color.FromArgb(255, 0x60, 0x6B, 0x7A),
    });

    bool _installed, _busy;
    string _status = "";
    public bool Installed { get => _installed; set { if (Set(ref _installed, value)) Raise(nameof(ActionText), nameof(ActionName), nameof(OpenVisibility)); } }
    public bool Busy { get => _busy; set { if (Set(ref _busy, value)) Raise(nameof(NotBusy), nameof(BusyVisibility)); } }
    public bool NotBusy => !_busy;
    public Visibility BusyVisibility => _busy ? Visibility.Visible : Visibility.Collapsed;
    public string Status { get => _status; set => Set(ref _status, value); }
    public bool CanInstall => App.Source.Installable;
    public string ActionText => !CanInstall ? "Open in TV store" : Installed ? "Reinstall latest" : "Install";
    /// <summary>Screen-reader names: every card's button says "Install", so name the app too.</summary>
    public string ActionName => $"{ActionText} {Name}";
    public string OpenName => $"Open {Name}";
    /// <summary>Accent only for a real install action; store-only apps get a plain button.</summary>
    public Microsoft.UI.Xaml.Style ActionStyle => (Microsoft.UI.Xaml.Style)Microsoft.UI.Xaml.Application.Current.Resources[CanInstall ? "AccentButtonStyle" : "DefaultButtonStyle"];
    public Visibility OpenVisibility => Installed ? Visibility.Visible : Visibility.Collapsed;
}

public sealed class AppItem(AppRow row) : Observable
{
    public AppRow Row { get; } = row;
    public string Package => Row.Package;
    public string Source => Row.Source;
    public string Name { get; } = Catalog.Apps.FirstOrDefault(a => a.Package == row.Package)?.Name ?? FriendlyName(row.Package);
    public Visibility DisabledVisibility => Row.Disabled ? Visibility.Visible : Visibility.Collapsed;
    public double RowOpacity => Row.Disabled ? 0.6 : 1;

    static string FriendlyName(string pkg)
    {
        var parts = pkg.Split('.');
        var last = parts.Reverse().FirstOrDefault(p => p is not ("android" or "tv" or "app" or "firetv" or "atv" or "leanback" or "release" or "free")) ?? parts[^1];
        return char.ToUpperInvariant(last[0]) + last[1..];
    }
}
