using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WheelConvert.Converters;
using WheelConvert.Core;

namespace WheelConvert.UI;

/// <summary>Designer : actions rapides (avec aperçu de la roue) + paramètres généraux.</summary>
public partial class SettingsWindow : Window
{
    private const int MaxActions = WheelPages.MaxSectors;

    private static readonly string[] Emojis =
    {
        "📁", "📂", "🌐", "🧮", "📝", "💻", "⚙", "🎵", "📷", "🖥", "🔒", "✂",
        "🚀", "📦", "💡", "🔧", "📊", "🎮", "📧", "⭐", "🏠", "🖨", "📅", "🔍"
    };

    private static readonly string[] Palette =
    {
        "#2D7DFF", "#3E63DD", "#8E4EC6", "#E54D9E", "#E5484D", "#F5A524", "#12A594", "#5B5B66"
    };

    private readonly AppSettings _draft;
    private readonly ObservableCollection<WheelPage> _wheels;
    private readonly ObservableCollection<QuickAction> _noActions = new();
    private readonly WheelControl _preview = new();
    private WheelPage? _wheel;
    private ExternalActions.ExternalWheel? _external; // roue Stream Deck sélectionnée (lecture seule)
    private string? _previewKey; // roue affichée dans l'aperçu (peut être une roue Stream Deck, en lecture seule)
    private bool _syncingTarget, _rebuilding;

    /// Liste déroulante : roues WheelConvert (WheelPage) puis roues Stream Deck (ExternalWheel, lecture seule).
    private readonly ObservableCollection<object> _choices = new();

    /// Ligne affichée pour un secteur d'une roue Stream Deck.
    private sealed record ExternalRow(string Name, string Glyph, string Color);

    public event Action? Saved;

    public SettingsWindow()
    {
        InitializeComponent();
        ThemeMode = ThemeMode.System; // thème Fluent clair/sombre selon Windows

        var icon = TrayService.LoadWpfIcon();
        Icon = icon;
        LogoImage.Source = icon;

        _draft = AppSettings.Current.Clone();
        if (_draft.Wheels.Count == 0) _draft.Wheels.Add(new WheelPage { Name = "Principale", Glyph = "🏠" });
        _wheels = new ObservableCollection<WheelPage>(_draft.Wheels);
        WheelBox.ItemsSource = _choices;

        TypeBox.ItemsSource = QuickActionRunner.TypeNames;

        PreviewHost.Child = _preview;
        _preview.Cursor = Cursors.Hand;
        _preview.MouseLeftButtonUp += OnPreviewClick;
        _preview.MouseWheel += OnPreviewWheel;

        BuildEmojiPanel();
        BuildColorPanel();
        LoadGeneral();

        RebuildChoices(WheelPages.KeyFor(_wheels[0])); // → OnWheelSelected → SelectWheel

        // Mise à jour en direct quand Stream Deck modifie ses roues
        ExternalActions.Changed += OnExternalChanged;
        Closed += (_, _) => ExternalActions.Changed -= OnExternalChanged;
    }

    private ObservableCollection<QuickAction> _actions => _wheel?.Actions ?? _noActions;

    private QuickAction? Selected => ActionList.SelectedItem as QuickAction;

    // ================= Roues =================

    private static string? KeyOf(object? o) => o switch
    {
        WheelPage w => WheelPages.KeyFor(w),
        ExternalActions.ExternalWheel e => e.Key,
        _ => null
    };

    /// Recrée la liste déroulante (roues WheelConvert + Stream Deck) et resélectionne la roue <paramref name="key"/>.
    private void RebuildChoices(string? key)
    {
        _rebuilding = true;
        _choices.Clear();
        foreach (var w in _wheels) _choices.Add(w);
        if (ExternalBox?.IsChecked == true)
            foreach (var e in ExternalActions.Wheels) _choices.Add(e);
        _rebuilding = false;

        var target = _choices.FirstOrDefault(c => KeyOf(c) == key) ?? _choices.FirstOrDefault();
        if (ReferenceEquals(WheelBox.SelectedItem, target)) OnWheelSelected(WheelBox, null!);
        else WheelBox.SelectedItem = target;
    }

    private void OnWheelSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_rebuilding) return;
        switch (WheelBox.SelectedItem)
        {
            case WheelPage w:
                SelectWheel(w);
                break;
            case ExternalActions.ExternalWheel ext:
                SelectExternal(ext);
                break;
        }
    }

    private void DetachWheel()
    {
        if (_wheel is null) return;
        _wheel.PropertyChanged -= OnWheelChanged;
        _wheel.Actions.CollectionChanged -= OnCollectionChanged;
        foreach (var a in _wheel.Actions) a.PropertyChanged -= OnActionChanged;
        _wheel = null;
    }

    /// Roue Stream Deck : affichée (aperçu + liste) mais pas modifiable ici.
    private void SelectExternal(ExternalActions.ExternalWheel ext)
    {
        DetachWheel();
        _external = ext;
        SetReadOnly(true);

        WheelNameBox.DataContext = ext;
        WheelGlyphBox.DataContext = ext;
        ActionList.ItemsSource = ext.Actions
            .Select(a => new ExternalRow(a.Name,
                !string.IsNullOrWhiteSpace(a.Glyph) ? a.Glyph! : ExternalActions.GlyphFor(a.Icon),
                string.IsNullOrWhiteSpace(a.Color) ? ext.Color : a.Color!))
            .ToList();
        _previewKey = ext.Key;
        UpdateEditor();
        RefreshPreview();
    }

    private void SetReadOnly(bool readOnly)
    {
        WheelNameBox.IsEnabled = WheelGlyphBox.IsEnabled = !readOnly;
        WheelLeftButton.IsEnabled = WheelRightButton.IsEnabled = DeleteWheelButton.IsEnabled = !readOnly;
        ListButtons.IsEnabled = !readOnly;
        ExternalHint.Visibility = readOnly ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SelectWheel(WheelPage w)
    {
        DetachWheel();
        _external = null;
        SetReadOnly(false);

        _wheel = w;
        w.PropertyChanged += OnWheelChanged;
        w.Actions.CollectionChanged += OnCollectionChanged;
        foreach (var a in w.Actions) a.PropertyChanged += OnActionChanged;

        WheelNameBox.DataContext = w;
        WheelGlyphBox.DataContext = w;
        ActionList.ItemsSource = w.Actions;
        _previewKey = WheelPages.KeyFor(w);

        if (w.Actions.Count > 0) ActionList.SelectedIndex = 0;
        else UpdateEditor();
        RefreshPreview();
    }

    private void OnWheelChanged(object? sender, PropertyChangedEventArgs e) => RefreshPreview();

    private void OnAddWheel(object sender, RoutedEventArgs e)
    {
        var w = new WheelPage
        {
            Name = $"Roue {_wheels.Count + 1}",
            Glyph = Emojis[(_wheels.Count * 5) % Emojis.Length],
            Color = Palette[_wheels.Count % Palette.Length]
        };
        _wheels.Add(w);
        RebuildChoices(WheelPages.KeyFor(w));
        WheelNameBox.Focus();
        WheelNameBox.SelectAll();
    }

    private void OnWheelLeft(object sender, RoutedEventArgs e) => MoveWheel(-1);
    private void OnWheelRight(object sender, RoutedEventArgs e) => MoveWheel(1);

    private void MoveWheel(int delta)
    {
        if (_wheel is null) return;
        int i = _wheels.IndexOf(_wheel), j = i + delta;
        if (i < 0 || j < 0 || j >= _wheels.Count) return;
        var w = _wheel;
        _wheels.Move(i, j);
        RebuildChoices(WheelPages.KeyFor(w));
    }

    private void OnDeleteWheel(object sender, RoutedEventArgs e)
    {
        if (_wheel is null) return;
        if (_wheels.Count <= 1)
        {
            MessageBox.Show(this, "Il faut garder au moins une roue.", "WheelConvert", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_wheel.Actions.Count > 0 &&
            MessageBox.Show(this, $"Supprimer la roue « {_wheel.Name} » et ses {_wheel.Actions.Count} action(s) ?", "WheelConvert",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        int i = _wheels.IndexOf(_wheel);
        _wheels.RemoveAt(i);
        RebuildChoices(WheelPages.KeyFor(_wheels[Math.Min(i, _wheels.Count - 1)]));
    }

    /// Instantané des réglages en cours d'édition (pour l'aperçu et les liens entre roues).
    private AppSettings Snapshot()
    {
        _draft.Wheels = _wheels.ToList();
        _draft.ShowExternalActions = ExternalBox?.IsChecked == true;
        return _draft;
    }

    // ================= Aperçu / éditeur =================

    private void RefreshPreview()
    {
        var pages = WheelPages.All(Snapshot());
        int index = pages.FindIndex(p => p.Key == _previewKey);
        if (index < 0 && _wheel is not null)
        {
            _previewKey = WheelPages.KeyFor(_wheel);
            index = pages.FindIndex(p => p.Key == _previewKey);
        }
        if (index < 0)
        {
            _preview.SetItems(new List<WheelEntry>(), "Actions", "Aucune roue");
            return;
        }

        var page = pages[index];
        var entries = page.Build();
        string dots = pages.Count is > 1 and <= 7
            ? string.Concat(Enumerable.Range(0, pages.Count).Select(i => i == index ? "●" : "○")) + "\n"
            : pages.Count > 7 ? $"{index + 1}/{pages.Count}\n" : "";
        string info = page.Source is null ? $"{entries.Count} action(s)" : $"{page.Source} · lecture seule";

        _preview.SetItems(entries, $"{page.Glyph} {page.Name}", dots + info);
        _preview.SetHover(ActionList.SelectedIndex);
    }

    private void OnPreviewWheel(object sender, MouseWheelEventArgs e)
    {
        var pages = WheelPages.All(Snapshot());
        if (pages.Count == 0) return;
        int index = Math.Max(0, pages.FindIndex(p => p.Key == _previewKey));
        index = ((index + (e.Delta < 0 ? 1 : -1)) % pages.Count + pages.Count) % pages.Count;
        _previewKey = pages[index].Key;

        // La roue affichée devient aussi la roue sélectionnée (éditable ou lecture seule)
        var choice = _choices.FirstOrDefault(c => KeyOf(c) == _previewKey);
        if (choice is not null) WheelBox.SelectedItem = choice;
        else RefreshPreview();
        e.Handled = true;
    }

    private void UpdateExternalStatus()
    {
        var wheels = ExternalActions.Wheels;
        ExternalStatus.Text = wheels.Count == 0
            ? "Aucune roue reçue pour l'instant."
            : $"✔  {wheels.Count} roue(s) reçue(s) : " + string.Join(", ", wheels.Take(8).Select(w => $"{w.Name} ({w.Actions.Count})"))
              + (wheels.Count > 8 ? "…" : "");
    }

    private void OnExternalChanged() => Dispatcher.InvokeAsync(() =>
    {
        UpdateExternalStatus();
        RebuildChoices(KeyOf(WheelBox.SelectedItem));
    });

    private void UpdateEditor()
    {
        var a = Selected;
        EditorPanel.DataContext = a;
        EditorPanel.IsEnabled = a is not null;
        if (KeyOf(WheelBox.SelectedItem) == _previewKey) _preview.SetHover(ActionList.SelectedIndex);
        if (a is null) return;

        var (label, hint, browse, hasArgs) = QuickActionRunner.Describe(a.Type);
        bool link = a.Type == QuickActionType.OpenWheel;
        TargetLabel.Text = label;
        TargetHint.Text = hint;
        BrowseButton.Visibility = browse == BrowseKind.None ? Visibility.Collapsed : Visibility.Visible;
        ArgsLabel.Visibility = ArgsBox.Visibility = hasArgs ? Visibility.Visible : Visibility.Collapsed;
        bool noTarget = a.Type == QuickActionType.ScanScreenQr; // aucune cible à saisir
        TargetBox.Visibility = link || noTarget ? Visibility.Collapsed : Visibility.Visible;
        if (noTarget) BrowseButton.Visibility = Visibility.Collapsed;
        WheelTargetBox.Visibility = link ? Visibility.Visible : Visibility.Collapsed;

        if (link)
        {
            _syncingTarget = true;
            WheelTargetBox.ItemsSource = WheelPages.LinkChoices(Snapshot(), _wheel is null ? null : WheelPages.KeyFor(_wheel));
            WheelTargetBox.SelectedValue = a.Target;
            _syncingTarget = false;
        }
    }

    private void OnWheelTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingTarget || Selected is not { Type: QuickActionType.OpenWheel } a) return;
        if (WheelTargetBox.SelectedValue is not string key) return;
        a.Target = key;

        // Nom/icône par défaut d'après la roue visée
        var page = WheelPages.All(Snapshot()).FirstOrDefault(p => p.Key == key);
        if (page is not null && (a.Name == "Nouvelle action" || string.IsNullOrWhiteSpace(a.Name)))
        {
            a.Name = page.Name;
            a.Glyph = page.Glyph;
        }
    }

    private void OnActionChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshPreview();
        if (e.PropertyName == nameof(QuickAction.Type) && ReferenceEquals(sender, Selected)) UpdateEditor();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (QuickAction a in e.OldItems) a.PropertyChanged -= OnActionChanged;
        if (e.NewItems is not null)
            foreach (QuickAction a in e.NewItems) a.PropertyChanged += OnActionChanged;
        RefreshPreview();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateEditor();

    private void OnPreviewClick(object sender, MouseButtonEventArgs e)
    {
        if (KeyOf(WheelBox.SelectedItem) != _previewKey) return;
        int index = _preview.HitTest(e.GetPosition(_preview));
        if (index >= 0 && index < ActionList.Items.Count) ActionList.SelectedIndex = index;
    }

    private void BuildEmojiPanel()
    {
        var font = new FontFamily("Segoe UI Emoji");
        foreach (var emoji in Emojis)
        {
            var b = new Button
            {
                Content = emoji, FontFamily = font, FontSize = 16,
                Width = 40, Height = 36, Padding = new Thickness(0), Margin = new Thickness(0, 0, 4, 4)
            };
            b.Click += (_, _) => { if (Selected is { } a) a.Glyph = emoji; };
            EmojiPanel.Children.Add(b);
        }
    }

    private void BuildColorPanel()
    {
        foreach (var hex in Palette)
        {
            var swatch = new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(14),
                Background = (Brush)new BrushConverter().ConvertFromString(hex)!,
                Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand, ToolTip = hex
            };
            swatch.MouseLeftButtonUp += (_, _) => { if (Selected is { } a) a.Color = hex; };
            ColorPanel.Children.Add(swatch);
        }
    }

    // ================= Liste =================

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (_actions.Count >= MaxActions)
        {
            MessageBox.Show(this, $"Maximum {MaxActions} actions pour garder une roue lisible.", "WheelConvert",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var a = new QuickAction { Color = Palette[_actions.Count % Palette.Length] };
        _actions.Add(a);
        ActionList.SelectedItem = a;
    }

    private void OnDuplicate(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } a || _actions.Count >= MaxActions) return;
        var copy = JsonSerializer.Deserialize<QuickAction>(JsonSerializer.Serialize(a))!;
        copy.Name += " (copie)";
        _actions.Insert(ActionList.SelectedIndex + 1, copy);
        ActionList.SelectedItem = copy;
    }

    private void OnUp(object sender, RoutedEventArgs e)
    {
        int i = ActionList.SelectedIndex;
        if (i <= 0) return;
        _actions.Move(i, i - 1);
        ActionList.SelectedIndex = i - 1;
    }

    private void OnDown(object sender, RoutedEventArgs e)
    {
        int i = ActionList.SelectedIndex;
        if (i < 0 || i >= _actions.Count - 1) return;
        _actions.Move(i, i + 1);
        ActionList.SelectedIndex = i + 1;
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        int i = ActionList.SelectedIndex;
        if (i < 0) return;
        _actions.RemoveAt(i);
        if (_actions.Count > 0) ActionList.SelectedIndex = Math.Min(i, _actions.Count - 1);
        else UpdateEditor();
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } a) return;
        var (_, _, browse, _) = QuickActionRunner.Describe(a.Type);
        bool defaultName = a.Name == "Nouvelle action";

        if (browse == BrowseKind.Folder)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choisir un dossier" };
            if (dlg.ShowDialog(this) != true) return;
            a.Target = dlg.FolderName;
            if (defaultName) a.Name = System.IO.Path.GetFileName(dlg.FolderName.TrimEnd('\\'));
        }
        else
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choisir un fichier",
                Filter = a.Type == QuickActionType.PowerShell
                    ? "Scripts PowerShell|*.ps1|Tous les fichiers|*.*"
                    : "Programmes et raccourcis|*.exe;*.lnk;*.bat;*.cmd;*.ps1|Tous les fichiers|*.*"
            };
            if (dlg.ShowDialog(this) != true) return;
            a.Target = dlg.FileName;
            if (defaultName) a.Name = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
        }
    }

    private void OnTest(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } a) return;
        try
        {
            QuickActionRunner.Run(a);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Test de l'action", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ================= Général =================

    private void LoadGeneral()
    {
        ShakeBox.IsChecked = _draft.ShakeToOpen;
        ShiftDragBox.IsChecked = _draft.ShiftDuringDrag;
        QuickOnEmptyBox.IsChecked = _draft.QuickWheelOnEmptyDrag;
        DebugBox.IsChecked = _draft.DebugLog;
        ExternalBox.IsChecked = _draft.ShowExternalActions;
        RememberWheelBox.IsChecked = _draft.RememberLastWheel;
        ExternalBox.Checked += (_, _) => RebuildChoices(KeyOf(WheelBox.SelectedItem));
        ExternalBox.Unchecked += (_, _) => RebuildChoices(KeyOf(WheelBox.SelectedItem));
        UpdateExternalStatus();
        HotkeyBox.IsChecked = _draft.HotkeyEnabled;
        NotifBox.IsChecked = _draft.Notifications;
        RevealBox.IsChecked = _draft.RevealAfterConvert;
        StartupBox.IsChecked = StartupManager.IsEnabled();

        NextToRadio.IsChecked = _draft.Output == OutputMode.NextToSource;
        SubRadio.IsChecked = _draft.Output == OutputMode.SubFolder;
        SubFolderBox.Text = _draft.SubFolderName;
        CleanFoldersBox.Text = _draft.CleanZipFolders ?? CleanZip.DefaultFolders;
        CleanFilesBox.Text = _draft.CleanZipFiles ?? CleanZip.DefaultFiles;
        CleanMaxBox.Text = _draft.CleanZipMaxFileMb.ToString();

        JpegSlider.Value = _draft.JpegQuality;
        WebpSlider.Value = _draft.WebpQuality;

        FfmpegBox.Text = _draft.FfmpegPath ?? "";
        UpdateFfmpegStatus();
    }

    private void UpdateFfmpegStatus()
    {
        var path = MediaTools.TryLocate(FfmpegBox.Text);
        FfmpegStatus.Text = path is not null
            ? $"✔  FFmpeg trouvé : {path}"
            : "✖  FFmpeg introuvable — les conversions vidéo et audio ne fonctionneront pas.";
    }

    private void OnBrowseFfmpeg(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choisir ffmpeg.exe", Filter = "ffmpeg.exe|ffmpeg.exe|Programmes|*.exe" };
        if (dlg.ShowDialog(this) != true) return;
        FfmpegBox.Text = dlg.FileName;
        UpdateFfmpegStatus();
    }

    private void OnInstallFfmpeg(object sender, RoutedEventArgs e)
    {
        try
        {
            MediaTools.InstallWithWinget();
            MessageBox.Show(this,
                "L'installation de FFmpeg s'ouvre dans une console.\nUne fois terminée, clique sur « Revérifier ».",
                "FFmpeg", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"winget n'est pas disponible : {ex.Message}\nTélécharge FFmpeg sur gyan.dev et indique le chemin de ffmpeg.exe.",
                "FFmpeg", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnCheckFfmpeg(object sender, RoutedEventArgs e) => UpdateFfmpegStatus();

    // ================= Enregistrer =================

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _draft.Wheels = _wheels.ToList();
        _draft.RememberLastWheel = RememberWheelBox.IsChecked == true;
        _draft.ShakeToOpen = ShakeBox.IsChecked == true;
        _draft.ShiftDuringDrag = ShiftDragBox.IsChecked == true;
        _draft.QuickWheelOnEmptyDrag = QuickOnEmptyBox.IsChecked == true;
        _draft.DebugLog = DebugBox.IsChecked == true;
        _draft.ShowExternalActions = ExternalBox.IsChecked == true;
        _draft.HotkeyEnabled = HotkeyBox.IsChecked == true;
        _draft.Notifications = NotifBox.IsChecked == true;
        _draft.RevealAfterConvert = RevealBox.IsChecked == true;
        _draft.Output = SubRadio.IsChecked == true ? OutputMode.SubFolder : OutputMode.NextToSource;
        _draft.SubFolderName = string.IsNullOrWhiteSpace(SubFolderBox.Text) ? "Converti" : SubFolderBox.Text.Trim();
        // Listes identiques aux valeurs par défaut → null, pour profiter des futures mises à jour de ces listes
        _draft.CleanZipFolders = Normalize(CleanFoldersBox.Text) == Normalize(CleanZip.DefaultFolders) ? null : CleanFoldersBox.Text.Trim();
        _draft.CleanZipFiles = Normalize(CleanFilesBox.Text) == Normalize(CleanZip.DefaultFiles) ? null : CleanFilesBox.Text.Trim();
        _draft.CleanZipMaxFileMb = int.TryParse(CleanMaxBox.Text.Trim(), out int maxMb) && maxMb >= 0 ? maxMb : 50;
        _draft.JpegQuality = (int)JpegSlider.Value;
        _draft.WebpQuality = (int)WebpSlider.Value;
        _draft.FfmpegPath = string.IsNullOrWhiteSpace(FfmpegBox.Text) ? null : FfmpegBox.Text.Trim();

        try
        {
            AppSettings.Replace(_draft);
            StartupManager.Set(StartupBox.IsChecked == true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Enregistrement impossible", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        Saved?.Invoke();
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    // ================= ZIP propre =================

    private void OnResetCleanZip(object sender, RoutedEventArgs e)
    {
        CleanFoldersBox.Text = CleanZip.DefaultFolders;
        CleanFilesBox.Text = CleanZip.DefaultFiles;
        CleanMaxBox.Text = "50";
    }

    private static string Normalize(string list) =>
        string.Join(";", list.Split(new[] { ';', ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                             .Select(x => x.ToLowerInvariant()).OrderBy(x => x));
}
