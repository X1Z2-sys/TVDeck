using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using TVDeck.Core;
using Windows.ApplicationModel.DataTransfer;

namespace TVDeck.Pages;

public sealed partial class RecipesPage : Page
{
    public sealed class Entry
    {
        public required string? Path { get; init; }
        public required Recipe Recipe { get; init; }
        public bool BuiltIn { get; init; }
        public string Name => Recipe.Name;
        public string Summary => (BuiltIn ? "Built in · " : Recipe.Author is { Length: > 0 } a ? $"By {a} · " : "") + $"{Recipe.Steps.Count} steps";
        public override string ToString() => Name;   // screen readers announce list rows by this
    }

    public sealed class StepRow(RecipeStep step)
    {
        public RecipeStep Step { get; } = step;
        public string Text => Step.Describe(id => Catalog.Find(id));
        public override string ToString() => Text;
    }

    readonly ObservableCollection<StepRow> _steps = [];
    Entry? _current;
    CancellationTokenSource? _run;

    public RecipesPage()
    {
        InitializeComponent();
        Steps.ItemsSource = _steps;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) => Reload();

    void Reload(string? select = null)
    {
        var builtIn = Catalog.BuiltInRecipes();
        var entries = Catalog.Recipes().Select(r => new Entry { Path = r.Path, Recipe = r.Recipe })
            .Concat(builtIn.Select(r => new Entry { Path = null, Recipe = r, BuiltIn = true })).ToList();
        List.ItemsSource = entries;
        List.SelectedItem = entries.FirstOrDefault(x => x.Path == select && select is not null);
    }

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _current = List.SelectedItem as Entry;
        Detail.Visibility = _current is null ? Visibility.Collapsed : Visibility.Visible;
        NoneSelected.Visibility = _current is null ? Visibility.Visible : Visibility.Collapsed;
        if (_current is null) return;
        Show(_current.Recipe);
    }

    void Show(Recipe r)
    {
        NameBox.Text = r.Name;
        DescBox.Text = r.Description;
        AuthorBox.Text = r.Author;
        _steps.Clear();
        foreach (var s in r.Steps) _steps.Add(new StepRow(s));
        RunStatus.IsOpen = false;
    }

    Recipe Current() => new()
    {
        Name = NameBox.Text.Trim(),
        Description = DescBox.Text.Trim(),
        Author = AuthorBox.Text.Trim(),
        Steps = _steps.Select(s => s.Step).ToList(),
    };

    void New_Click(object sender, RoutedEventArgs e)
    {
        List.SelectedItem = null;
        _current = new Entry { Path = null, Recipe = new Recipe { Name = "My setup" } };
        Detail.Visibility = Visibility.Visible;
        NoneSelected.Visibility = Visibility.Collapsed;
        Show(_current.Recipe);
        NameBox.Focus(FocusState.Programmatic);
        NameBox.SelectAll();
    }

    async void Import_Click(object sender, RoutedEventArgs e)
    {
        var text = await Dialogs.Prompt(XamlRoot, "Paste a recipe", "Share code (tvdeck:…) or recipe JSON", "Import", multiline: true);
        if (string.IsNullOrWhiteSpace(text)) return;
        await Session.Try(async () =>
        {
            var r = Catalog.ParseShared(text);
            var preview = string.Join('\n', r.Steps.Select((s, i) => $"{i + 1}. {s.Describe(id => Catalog.Find(id))}"));
            if (!await Dialogs.Confirm(XamlRoot, $"Import \"{r.Name}\"?", $"{(r.Author.Length > 0 ? $"By {r.Author}. " : "")}{r.Description}\n\nIt will:\n{preview}\n\nOnly import recipes from people you trust; review every command.", "Import"))
                return;
            Reload(Catalog.SaveRecipe(r));
            Session.Notify($"\"{r.Name}\" added to your recipes.", "Imported");
        }, "That recipe couldn't be imported");
    }

    async void Save_Click(object sender, RoutedEventArgs e)
    {
        await Session.Try(() =>
        {
            var r = Current();
            if (_current?.Path is { } old && System.IO.Path.GetFileNameWithoutExtension(old) != r.Name) File.Delete(old);
            var path = Catalog.SaveRecipe(r);
            Reload(path);
            Session.Notify($"\"{r.Name}\" saved.", "Saved");
            return Task.CompletedTask;
        }, "Couldn't save the recipe");
    }

    async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        if (_current.BuiltIn) { Session.Notify("Built-in recipes can't be deleted. Save a copy under a new name to change it.", "Built in", InfoBarSeverity.Informational); return; }
        if (_current.Path is null) { Reload(); return; }
        if (!await Dialogs.Confirm(XamlRoot, $"Delete \"{_current.Name}\"?", "This removes the recipe from this PC.", "Delete")) return;
        File.Delete(_current.Path);
        Reload();
    }

    async void Share_Click(object sender, RoutedEventArgs e)
    {
        await Session.Try(() =>
        {
            var code = Catalog.ShareCode(Current());
            var dp = new DataPackage();
            dp.SetText(code);
            Clipboard.SetContent(dp);
            Session.Notify($"Share code copied ({code.Length} characters). Paste it anywhere — friends import it with Paste code.", "Copied");
            return Task.CompletedTask;
        }, "Couldn't create a share code");
    }

    async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_run is not null) { _run.Cancel(); return; }
        Recipe r;
        string serial;
        try { r = Current(); Catalog.ValidateRecipe(r); serial = Session.Require(); }
        catch (Exception ex) when (ex is InvalidOperationException or AdbException) { Session.Error(ex, "Can't run yet"); return; }

        var shellSteps = r.Steps.Where(s => s.Shell is not null).Select(s => s.Shell!).ToList();
        if (shellSteps.Count > 0 && !await Dialogs.Confirm(XamlRoot, "Run these commands on your TV?", string.Join('\n', shellSteps), "Run"))
            return;

        _run = new CancellationTokenSource();
        RunBtn.Content = "Cancel";
        RunStatus.Severity = InfoBarSeverity.Informational;
        RunStatus.Title = "Running";
        RunStatus.Message = "";
        RunProgress.Visibility = Visibility.Visible;
        RunStatus.IsOpen = true;
        try
        {
            var notes = await Catalog.RunRecipeAsync(serial, r, new Progress<string>(m => RunStatus.Message = m), _run.Token);
            RunStatus.Severity = notes.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            RunStatus.Title = "Done";
            RunStatus.Message = $"\"{r.Name}\" finished." + string.Concat(notes.Select(n => "\n• " + n));
        }
        catch (OperationCanceledException)
        {
            RunStatus.Severity = InfoBarSeverity.Warning;
            RunStatus.Title = "Stopped";
            RunStatus.Message = "The recipe was cancelled. Steps already finished stay applied.";
        }
        catch (Exception ex) when (ex is AdbException or InvalidOperationException or HttpRequestException or IOException)
        {
            RunStatus.Severity = InfoBarSeverity.Error;
            RunStatus.Title = "Failed";
            RunStatus.Message = ex.Message;
        }
        finally
        {
            RunProgress.Visibility = Visibility.Collapsed;
            _run = null;
            RunBtn.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new FontIcon { Glyph = "\uE768", FontSize = 14 }, new TextBlock { Text = "Run on TV" } } };
        }
    }

    void RemoveStep_Click(object sender, RoutedEventArgs e) => _steps.Remove((StepRow)((FrameworkElement)sender).Tag);

    async void AddInstall_Click(object sender, RoutedEventArgs e)
    {
        var box = new ListView
        {
            SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = 420, MinWidth = 420,
            ItemsSource = Catalog.Apps.Where(a => a.Source.Installable).OrderBy(a => a.Name).ToList(),
            DisplayMemberPath = "Name",
        };
        var d = new ContentDialog { XamlRoot = XamlRoot, Title = "Install apps", Content = box, PrimaryButtonText = "Add", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        if (await d.ShowAsync() != ContentDialogResult.Primary) return;
        foreach (CatalogApp a in box.SelectedItems) _steps.Add(new StepRow(new RecipeStep { Install = a.Id }));
    }

    async void AddCustom_Click(object sender, RoutedEventArgs e)
    {
        if (await AppEditor.Show(XamlRoot) is { } app) _steps.Add(new StepRow(new RecipeStep { App = app }));
    }

    async void AddLauncher_Click(object sender, RoutedEventArgs e)
    {
        var launchers = Catalog.Apps.Where(a => a.Category == "Launchers").OrderBy(a => a.Name).ToList();
        var box = new ComboBox { Header = "Launcher", ItemsSource = launchers, DisplayMemberPath = "Name", SelectedIndex = 0, MinWidth = 360, IsEditable = false };
        var d = new ContentDialog { XamlRoot = XamlRoot, Title = "Set the home screen", Content = box, PrimaryButtonText = "Add", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        if (await d.ShowAsync() == ContentDialogResult.Primary && box.SelectedItem is CatalogApp a)
            _steps.Add(new StepRow(new RecipeStep { Launcher = a.Package }));
    }

    async void AddLaunch_Click(object sender, RoutedEventArgs e)
    {
        var pkg = await Dialogs.Prompt(XamlRoot, "Open an app", "Package name", "Add", "org.jellyfin.androidtv");
        if (string.IsNullOrWhiteSpace(pkg)) return;
        await Session.Try(() => { Adb.RequirePackage(pkg.Trim()); _steps.Add(new StepRow(new RecipeStep { Launch = pkg.Trim() })); return Task.CompletedTask; }, "Invalid package");
    }

    async void AddShell_Click(object sender, RoutedEventArgs e)
    {
        var cmd = await Dialogs.Prompt(XamlRoot, "Run a command", "adb shell command", "Add", "settings put global private_dns_mode hostname");
        if (!string.IsNullOrWhiteSpace(cmd)) _steps.Add(new StepRow(new RecipeStep { Shell = cmd.Trim() }));
    }
}
