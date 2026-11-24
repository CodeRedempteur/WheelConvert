using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WheelConvert.Core;

public enum QuickActionType { OpenApp, OpenFolder, OpenUrl, RunCommand, PowerShell, SendKeys, PasteText, OpenWheel, ScanScreenQr }

public enum OutputMode { NextToSource, SubFolder }

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>Une action rapide configurable dans le designer.</summary>
public sealed class QuickAction : Observable
{
    private string _name = "Nouvelle action";
    private string _glyph = "⭐";
    private QuickActionType _type = QuickActionType.OpenApp;
    private string _target = "";
    private string _arguments = "";
    private string _color = "#2D7DFF";

    public string Name { get => _name; set => Set(ref _name, value); }
    public string Glyph { get => _glyph; set => Set(ref _glyph, value); }
    public QuickActionType Type { get => _type; set => Set(ref _type, value); }
    /// Pour OpenWheel : clé de la roue à ouvrir (voir WheelPages).
    public string Target { get => _target; set => Set(ref _target, value); }
    public string Arguments { get => _arguments; set => Set(ref _arguments, value); }
    public string Color { get => _color; set => Set(ref _color, value); }
}

/// <summary>Une roue d'actions (comme une page) : on passe de l'une à l'autre à la molette.</summary>
public sealed class WheelPage : Observable
{
    private string _name = "Nouvelle roue";
    private string _glyph = "⭐";
    private string _color = "#2D7DFF";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Glyph { get => _glyph; set => Set(ref _glyph, value); }
    public string Color { get => _color; set => Set(ref _color, value); }
    public ObservableCollection<QuickAction> Actions { get; set; } = new();
}

/// <summary>Paramètres persistés dans %AppData%\WheelConvert\settings.json.</summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 3;

    /// Roues d'actions (v3). La première s'ouvre par défaut.
    public List<WheelPage> Wheels { get; set; } = new();

    /// Ancien format (v2, une seule roue) — migré dans Wheels au chargement.
    public List<QuickAction>? QuickActions { get; set; }

    public bool ExplorerOnly { get; set; } = true;          // (ancien, non utilisé)
    public bool QuickWheelOnEmptyDrag { get; set; } = true; // Shift + clic droit → actions
    public bool ShakeToOpen { get; set; } = false;          // secouer pendant le glisser → conversion (optionnel)
    public bool ShiftDuringDrag { get; set; } = true;       // clic gauche + glisser, PUIS Shift → conversion
    public bool DebugLog { get; set; } = true;              // %AppData%\WheelConvert\log.txt
    public bool ShowExternalActions { get; set; } = true;   // roues Stream Deck (providers\*.json)
    public bool RememberLastWheel { get; set; } = true;     // rouvre la dernière roue utilisée
    public bool HotkeyEnabled { get; set; } = true;
    public bool Notifications { get; set; } = true;
    public bool RevealAfterConvert { get; set; } = true;    // ouvre le dossier et sélectionne le fichier créé

    // ZIP propre (dossier de projet) — null = listes par défaut (voir Converters/CleanZip)
    public string? CleanZipFolders { get; set; }
    public string? CleanZipFiles { get; set; }
    public int CleanZipMaxFileMb { get; set; } = 50;        // 0 = pas de limite
    public OutputMode Output { get; set; } = OutputMode.NextToSource;
    public string SubFolderName { get; set; } = "Converti";
    public int JpegQuality { get; set; } = 92;
    public int WebpQuality { get; set; } = 90;
    public string? FfmpegPath { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WheelConvert");

    public static string FilePath => Path.Combine(Folder, "settings.json");

    public static bool Exists => File.Exists(FilePath);

    private static AppSettings? _current;
    public static AppSettings Current => _current ??= Load();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options);
                if (s is not null)
                {
                    // Ancien format : on réécrit tout de suite en v3 (Stream Deck lit ce fichier pour afficher les roues)
                    if (s.Migrate()) try { s.Save(); } catch { }
                    return s;
                }
            }
        }
        catch { /* fichier corrompu : on repart des valeurs par défaut */ }
        return CreateDefault();
    }

    /// v2 → v3 : l'ancienne liste d'actions devient la roue « Principale ».
    private bool Migrate()
    {
        bool changed = Version < 3 || QuickActions is not null || Wheels.Count == 0;
        if (QuickActions is { Count: > 0 } && Wheels.Count == 0)
            Wheels.Add(new WheelPage { Name = "Principale", Glyph = "🏠", Actions = new(QuickActions) });
        QuickActions = null;
        if (Wheels.Count == 0) Wheels.Add(DefaultWheel());
        Version = 3;
        return changed;
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, Options), Options)!;

    public static void Replace(AppSettings settings)
    {
        settings.Save();
        _current = settings;
    }

    public static AppSettings CreateDefault() => new() { Wheels = { DefaultWheel() } };

    private static WheelPage DefaultWheel() => new()
    {
        Name = "Principale",
        Glyph = "🏠",
        Actions =
        {
            new QuickAction { Name = "Explorateur", Glyph = "📁", Type = QuickActionType.OpenApp, Target = "explorer.exe", Color = "#2D7DFF" },
            new QuickAction { Name = "Bloc-notes", Glyph = "📝", Type = QuickActionType.OpenApp, Target = "notepad.exe", Color = "#F5A524" },
            new QuickAction { Name = "Calculatrice", Glyph = "🧮", Type = QuickActionType.OpenApp, Target = "calc.exe", Color = "#12A594" },
            new QuickAction { Name = "Téléchargements", Glyph = "📂", Type = QuickActionType.OpenFolder, Target = @"%USERPROFILE%\Downloads", Color = "#8E4EC6" },
            new QuickAction { Name = "Capture", Glyph = "✂", Type = QuickActionType.OpenUrl, Target = "ms-screenclip:", Color = "#E5484D" },
            new QuickAction { Name = "Tâches", Glyph = "📊", Type = QuickActionType.OpenApp, Target = "taskmgr.exe", Color = "#5B5B66" },
            new QuickAction { Name = "QR écran", Glyph = "🔳", Type = QuickActionType.ScanScreenQr, Color = "#3E63DD" }
        }
    };
}
