using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WheelConvert.Core;

/// <summary>
/// Roues fournies par d'autres applications (ex. Stream Deck Script Builder).
/// Contrat : chaque application écrit un fichier JSON dans %AppData%\WheelConvert\providers\
/// (ex. streamdeck.json) ; WheelConvert le surveille et ajoute ses roues en direct.
///
/// v2 (plusieurs roues) :
/// { "provider": "Stream Deck", "version": 2,
///   "wheels": [ { "id": "w1", "name": "Dev", "icon": "fa-solid fa-code", "color": "#2D7DFF",
///                 "actions": [ { "id": "a1", "name": "Ceres", "icon": "fa-solid fa-code", "color": "#2D7DFF",
///                                "run": { "type": "powershell", "target": "C:\\...\\Script\\ceres.ps1" } },
///                              { "id": "a2", "name": "Jeux", "run": { "type": "wheel", "target": "w2" } } ] } ] }
///
/// v1 (une seule roue) : { "provider": "...", "actions": [ ... ] }
///
/// run.type : powershell (.ps1 ou commande, run.shell = exe PowerShell optionnel) · http (GET)
///            · open (programme, fichier, URL) · command (cmd /c) · wheel (ouvre la roue target du même fournisseur)
/// </summary>
public static class ExternalActions
{
    public sealed class RunSpec
    {
        public string Type { get; set; } = "http";
        public string Target { get; set; } = "";
        public string? Arguments { get; set; }
        public string? Shell { get; set; }
    }

    public sealed class Item
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Icon { get; set; }   // classe Font Awesome (convertie en emoji)
        public string? Glyph { get; set; }  // emoji direct (prioritaire)
        public string? Color { get; set; }
        public RunSpec Run { get; set; } = new();
    }

    public sealed class WheelDef
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Icon { get; set; }
        public string? Glyph { get; set; }
        public string? Color { get; set; }
        public List<Item> Actions { get; set; } = new();
    }

    public sealed class ProviderFile
    {
        public string Provider { get; set; } = "";
        public int Version { get; set; } = 1;
        public List<Item>? Actions { get; set; }
        public List<WheelDef>? Wheels { get; set; }
    }

    /// Roue externe prête à afficher. <see cref="Key"/> est unique tous fournisseurs confondus.
    public sealed record ExternalWheel(string Key, string Provider, string Name, string Glyph, string Color, IReadOnlyList<Item> Actions, string ProviderKey);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly object Gate = new();

    private static List<ExternalWheel> _wheels = new();
    private static FileSystemWatcher? _watcher;
    private static Timer? _debounce;

    /// Déclenché (sur un thread du pool) quand un fichier fournisseur change.
    public static event Action? Changed;

    public static string Folder => Path.Combine(AppSettings.Folder, "providers");

    public static IReadOnlyList<ExternalWheel> Wheels
    {
        get { lock (Gate) return _wheels.ToList(); }
    }

    public static int ActionCount => Wheels.Sum(w => w.Actions.Count);

    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            Reload();

            _debounce = new Timer(_ => { Reload(); Changed?.Invoke(); });
            _watcher = new FileSystemWatcher(Folder, "*.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            FileSystemEventHandler onChange = (_, _) => _debounce.Change(300, Timeout.Infinite);
            _watcher.Changed += onChange;
            _watcher.Created += onChange;
            _watcher.Deleted += onChange;
            _watcher.Renamed += (_, _) => _debounce.Change(300, Timeout.Infinite);
        }
        catch (Exception ex)
        {
            Log.Write($"Roues externes indisponibles : {ex.Message}");
        }
    }

    private static void Reload()
    {
        var list = new List<ExternalWheel>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(Folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var data = JsonSerializer.Deserialize<ProviderFile>(ReadShared(file), Json);
                    if (data is null) continue;

                    string providerKey = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                    string provider = string.IsNullOrWhiteSpace(data.Provider) ? providerKey : data.Provider;

                    var defs = data.Wheels is { Count: > 0 }
                        ? data.Wheels
                        : data.Actions is { Count: > 0 }
                            ? new List<WheelDef> { new() { Id = "main", Name = provider, Actions = data.Actions } }
                            : new List<WheelDef>();

                    foreach (var w in defs)
                    {
                        var actions = (w.Actions ?? new()).Where(a => !string.IsNullOrWhiteSpace(a.Name) && !string.IsNullOrWhiteSpace(a.Run?.Target)).ToList();
                        string id = string.IsNullOrWhiteSpace(w.Id) ? w.Name : w.Id;
                        list.Add(new ExternalWheel(
                            KeyFor(providerKey, id), provider,
                            string.IsNullOrWhiteSpace(w.Name) ? provider : w.Name,
                            !string.IsNullOrWhiteSpace(w.Glyph) ? w.Glyph! : GlyphFor(w.Icon),
                            string.IsNullOrWhiteSpace(w.Color) ? "#3E63DD" : w.Color!,
                            actions, providerKey));
                    }
                }
                catch (Exception ex)
                {
                    Log.Write($"Fichier fournisseur illisible {Path.GetFileName(file)} : {ex.Message}");
                }
            }
        }
        catch { }

        lock (Gate) _wheels = list;
        Log.Write($"Roues externes chargées : {list.Count} ({list.Sum(w => w.Actions.Count)} actions)");
    }

    public static string KeyFor(string providerKey, string wheelId) => $"ext:{providerKey}:{wheelId}";

    /// Lecture tolérante pendant que l'autre application écrit encore.
    private static string ReadShared(string path)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                return reader.ReadToEnd();
            }
            catch (IOException) when (i < 4)
            {
                Thread.Sleep(80);
            }
        }
    }

    // ================= Roue =================

    public static List<WheelEntry> EntriesFor(ExternalWheel wheel) =>
        wheel.Actions.Select(a =>
        {
            string glyph = !string.IsNullOrWhiteSpace(a.Glyph) ? a.Glyph! : GlyphFor(a.Icon);
            string color = string.IsNullOrWhiteSpace(a.Color) ? wheel.Color : a.Color!;

            if (a.Run.Type.Equals("wheel", StringComparison.OrdinalIgnoreCase))
                return new WheelEntry { Label = a.Name, Sub = "roue ›", Glyph = glyph, Color = color, SwitchTo = KeyFor(wheel.ProviderKey, a.Run.Target) };

            return new WheelEntry { Label = a.Name, Glyph = glyph, Color = color, Run = () => RunAsync(a) };
        }).ToList();

    public static async Task<string?> RunAsync(Item a)
    {
        var run = a.Run;
        string target = Environment.ExpandEnvironmentVariables(run.Target).Trim();
        string args = Environment.ExpandEnvironmentVariables(run.Arguments ?? "").Trim();
        Log.Write($"Action externe « {a.Name} » ({run.Type}) → {target}");

        switch (run.Type.ToLowerInvariant())
        {
            case "http":
                using (var response = await Http.GetAsync(target))
                {
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException($"« {a.Name} » : le serveur a répondu {(int)response.StatusCode} {response.ReasonPhrase}.");
                }
                return null;

            case "powershell":
                if (target.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) && !File.Exists(target))
                    throw new FileNotFoundException($"« {a.Name} » : script introuvable ({target}).");
                var psi = new ProcessStartInfo(string.IsNullOrWhiteSpace(run.Shell) ? "powershell.exe" : run.Shell)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                foreach (var p in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass" }) psi.ArgumentList.Add(p);
                if (target.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
                {
                    psi.ArgumentList.Add("-File");
                    psi.ArgumentList.Add(target);
                    psi.WorkingDirectory = Path.GetDirectoryName(target) ?? "";
                    foreach (var part in args.Split(' ', StringSplitOptions.RemoveEmptyEntries)) psi.ArgumentList.Add(part);
                }
                else
                {
                    psi.ArgumentList.Add("-Command");
                    psi.ArgumentList.Add(target);
                }
                Process.Start(psi);
                return null;

            case "command":
                Process.Start(new ProcessStartInfo("cmd.exe", "/c " + target) { UseShellExecute = false, CreateNoWindow = true });
                return null;

            default: // open
                Process.Start(new ProcessStartInfo(target) { Arguments = args, UseShellExecute = true });
                return null;
        }
    }

    // Icônes Font Awesome du Stream Deck → emoji (la roue WPF n'embarque pas Font Awesome)
    private static readonly (string Key, string Glyph)[] FaMap =
    {
        ("spotify", "🎵"), ("music", "🎵"), ("headphones", "🎧"), ("volume-xmark", "🔇"), ("volume-off", "🔈"),
        ("volume-low", "🔉"), ("volume", "🔊"), ("microphone", "🎤"), ("play", "▶"), ("pause", "⏸"), ("stop", "⏹"),
        ("forward", "⏭"), ("backward", "⏮"), ("chrome", "🌐"), ("globe", "🌐"), ("firefox", "🌐"), ("edge", "🌐"),
        ("youtube", "📺"), ("twitch", "📺"), ("discord", "💬"), ("telegram", "💬"), ("whatsapp", "💬"),
        ("comment", "💬"), ("envelope", "📧"), ("code", "💻"), ("terminal", "💻"), ("laptop", "💻"),
        ("desktop", "🖥"), ("display", "🖥"), ("gamepad", "🎮"), ("steam", "🎮"), ("xbox", "🎮"), ("playstation", "🎮"),
        ("power-off", "⏻"), ("moon", "🌙"), ("sun", "☀"), ("lightbulb", "💡"), ("bolt", "⚡"), ("lock", "🔒"),
        ("folder", "📁"), ("file", "📄"), ("camera", "📷"), ("video", "🎬"), ("image", "🖼"), ("calendar", "📅"),
        ("clock", "⏰"), ("stopwatch", "⏱"), ("bell", "🔔"), ("gear", "⚙"), ("wrench", "🔧"), ("house", "🏠"),
        ("rocket", "🚀"), ("star", "⭐"), ("heart", "❤"), ("fire", "🔥"), ("cart", "🛒"), ("print", "🖨"),
        ("wifi", "📶"), ("network", "📶"), ("server", "🗄"), ("database", "🗄"), ("cloud", "☁"), ("download", "⬇"),
        ("upload", "⬆"), ("rotate", "🔄"), ("arrows", "🔄"), ("magnifying", "🔍"), ("search", "🔍"), ("user", "👤"),
        ("keyboard", "⌨"), ("mouse", "🖱"), ("window", "🗔"), ("expand", "⛶"), ("chart", "📊"), ("list", "📋"),
        ("note", "📝"), ("pen", "✏"), ("scissors", "✂"), ("trash", "🗑"), ("tv", "📺"), ("film", "🎞"),
        ("scroll", "📜"), ("circle-notch", "🎛"), ("layer", "🗂"), ("briefcase", "💼"), ("book", "📖")
    };

    public static string GlyphFor(string? faClass)
    {
        if (string.IsNullOrWhiteSpace(faClass)) return "🎛";
        string c = faClass.ToLowerInvariant();
        foreach (var (key, glyph) in FaMap)
            if (c.Contains("fa-" + key)) return glyph;
        return "🎛";
    }
}
