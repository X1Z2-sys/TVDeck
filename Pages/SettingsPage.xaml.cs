using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TVDeck.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        Theme.SelectedIndex = (int)MainWindow.SavedTheme;   // buttons are in AppTheme order
    }

    void Theme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Theme.SelectedIndex >= 0) MainWindow.Instance.ApplyTheme((MainWindow.AppTheme)Theme.SelectedIndex);
    }
}
