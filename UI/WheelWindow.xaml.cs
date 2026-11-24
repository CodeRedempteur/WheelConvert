using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using WheelConvert.Converters;
using WheelConvert.Core;

namespace WheelConvert.UI;

/// <summary>
/// Fenêtre flottante de la roue.
///  • Glisser des fichiers + secouer (ou Shift) → roue de conversion, déposer sur un format.
///  • Shift + clic droit maintenu                 → roue d'actions rapides, relâcher sur une action.
///  • Ctrl + Alt + Espace                         → roue d'actions rapides au curseur (clic).
/// </summary>
public partial class WheelWindow : Window
{
    private enum Mode { Hidden, Pending, Files, Radial, Hotkey }

    private const int HotkeyId = 0x5743;

    private readonly WheelControl _wheel = new();
    private readonly DispatcherTimer _pendingTimer;
    private readonly DispatcherTimer _dwellTimer;
    private readonly IntPtr _hwnd;

    private Mode _mode = Mode.Hidden;
    private List<WheelEntry> _entries = new();
    private string? _dragKey;
    private int _left, _top, _px = 1, _session, _lastX, _lastY;
    private IntPtr _prevForeground;

    // Roues d'actions (pages)
    private List<WheelPages.Page> _pages = new();
    private int _pageIndex, _dwellIndex = -1;
    private string? _lastPageKey;

    public event Action<string, string, bool>? Notify;

    public bool Paused { get; set; }

    public WheelWindow()
    {
        InitializeComponent();
        Host.Child = _wheel;

        _wheel.MouseMove += (_, e) =>
        {
            if (_mode == Mode.Hotkey) SetHover(_wheel.HitTest(e.GetPosition(_wheel)));
        };
        _wheel.MouseLeftButtonUp += (_, e) =>
        {
            if (_mode != Mode.Hotkey) return;
            var entry = Pick(_wheel.HitTest(e.GetPosition(_wheel)));
            if (entry?.SwitchTo is { } key)
            {
                SwitchTo(key);
                return;
            }
            HideWheel();
            if (entry is not null) Execute(entry);
        };
        // Molette en mode clic (Ctrl+Alt+Espace) : la fenêtre a le focus, pas besoin du hook
        PreviewMouseWheel += (_, e) =>
        {
            if (_mode != Mode.Hotkey) return;
            ShowPage(_pageIndex + (e.Delta < 0 ? 1 : -1));
            e.Handled = true;
        };

        // Survol prolongé d'un secteur « lien » → bascule de roue
        _dwellTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _dwellTimer.Tick += (_, _) => OnDwell();

        // Pas de fichiers reçus après l'apparition : ce n'était pas un glisser de fichiers
        _pendingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _pendingTimer.Tick += (_, _) =>
        {
            _pendingTimer.Stop();
            if (_mode != Mode.Pending) return;
            Log.Write("Aucun fichier reçu → roue masquée (glisser sans fichier, ou dépôt refusé par la source)");
            HideWheel();
        };

        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
    }

    /// Crée les visuels à l'avance pour un premier affichage instantané.
    public void Prewarm()
    {
        Show();
        Hide();
    }

    // ================= Roue de conversion (glisser de fichiers) =================

    public void BeginDragSession(int x, int y, string reason)
    {
        if (Paused) return;
        Log.Write($"Glisser détecté ({reason}) à {x},{y} → attente des fichiers");
        _session++;
        _dragKey = null;
        _mode = Mode.Pending;
        ShowEntries(new List<WheelEntry>(), "Fichiers", "Bouge un peu…");
        PlaceAt(x, y, activate: false);
        _pendingTimer.Stop();
        _pendingTimer.Start();
    }

    public async void OnHookButtonUp(int x, int y)
    {
        switch (_mode)
        {
            case Mode.Pending:
                HideWheel();
                break;

            case Mode.Files:
                // Laisse le temps à l'événement Drop d'arriver
                int session = _session;
                await Task.Delay(450);
                if (session == _session && _mode == Mode.Files) HideWheel();
                break;
        }
    }

    private bool TryEnterFiles(DragEventArgs e)
    {
        string[]? files = null;
        try { files = e.Data.GetData(DataFormats.FileDrop) as string[]; } catch { }

        if (files is null || files.Length == 0)
        {
            if (_mode == Mode.Pending && _dragKey is null)
            {
                _dragKey = "";
                Log.Write("Glisser reçu sans fichiers. Formats : " + string.Join(", ", SafeFormats(e)));
            }
            return _mode == Mode.Files;
        }

        string key = string.Join("|", files);
        if (_mode == Mode.Files && key == _dragKey) return true;

        _dragKey = key;
        _pendingTimer.Stop();
        _mode = Mode.Files;
        var (title, entries) = ConversionCatalog.For(files);
        ShowEntries(entries, title, "Déposer sur un format");
        Log.Write($"{files.Length} fichier(s) reçu(s) : {string.Join(" | ", files.Take(3))}");
        return true;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!TryEnterFiles(e))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        int index = _wheel.HitTest(e.GetPosition(_wheel));
        _wheel.SetHover(index);
        // Toujours « Copier » : l'original n'est jamais déplacé
        e.Effects = Pick(index) is not null ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => _wheel.SetHover(-1);

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var entry = _mode == Mode.Files ? Pick(_wheel.HitTest(e.GetPosition(_wheel))) : null;
        e.Effects = entry is not null ? DragDropEffects.Copy : DragDropEffects.None;
        HideWheel();
        Log.Write(entry is null ? "Dépôt hors secteur → annulé" : $"Dépôt sur « {entry.Label} »");
        // Rend la main à l'Explorateur avant de travailler
        if (entry is not null) Dispatcher.InvokeAsync(() => Execute(entry));
    }

    private static IEnumerable<string> SafeFormats(DragEventArgs e)
    {
        try { return e.Data.GetFormats(); } catch { return Array.Empty<string>(); }
    }

    // ================= Roue d'actions (Shift + clic droit) =================

    public void BeginQuickSession(int x, int y)
    {
        if (Paused) return;
        _session++;
        _pendingTimer.Stop();
        _prevForeground = GetForegroundWindow(); // pour les raccourcis clavier / collage
        if (_prevForeground == _hwnd) _prevForeground = IntPtr.Zero;

        _mode = Mode.Radial;
        _lastX = x;
        _lastY = y;
        LoadPages();
        PlaceAt(x, y, activate: false);
        ShowPage(StartPageIndex());
    }

    public void OnHookMove(int x, int y)
    {
        _lastX = x;
        _lastY = y;
        if (_mode == Mode.Radial) SetHover(_wheel.HitTest(ScreenToLocal(x, y)));
    }

    public void OnHookScroll(int step)
    {
        if (_mode is Mode.Radial or Mode.Hotkey) ShowPage(_pageIndex + step);
    }

    public void OnQuickUp(int x, int y)
    {
        if (_mode != Mode.Radial) return;
        var entry = Pick(_wheel.HitTest(ScreenToLocal(x, y)));

        // Relâché sur un lien de roue : on bascule et la roue reste ouverte (mode clic)
        if (entry?.SwitchTo is { } key)
        {
            _dwellTimer.Stop();
            SwitchTo(key);
            EnterClickMode();
            return;
        }

        HideWheel();
        if (entry is not null) Execute(entry);
    }

    // ================= Ctrl + Alt + Espace =================

    public void ShowQuickWheelAtCursor()
    {
        if (Paused) return;
        _session++;
        _pendingTimer.Stop();
        _prevForeground = GetForegroundWindow();
        if (_prevForeground == _hwnd) _prevForeground = IntPtr.Zero;
        GetCursorPos(out var p);

        _mode = Mode.Hotkey;
        LoadPages();
        ShowPage(StartPageIndex());
        PlaceAt(p.X, p.Y, activate: true);
        Activate();
        Focus();
    }

    /// Garde la roue ouverte en mode clic (après un changement de roue au relâchement).
    private void EnterClickMode()
    {
        _mode = Mode.Hotkey;
        ShowPage(_pageIndex);
        SetWindowPos(_hwnd, HWND_TOPMOST, _left, _top, _px, _px, SWP_SHOWWINDOW);
        Activate();
        Focus();
    }

    private void HideWheel()
    {
        _pendingTimer.Stop();
        _dwellTimer.Stop();
        _mode = Mode.Hidden;
        _wheel.SetHover(-1);
        Hide();
    }

    // ================= Pages de roues =================

    private void LoadPages() => _pages = WheelPages.All();

    private int StartPageIndex()
    {
        if (!AppSettings.Current.RememberLastWheel || _lastPageKey is null) return 0;
        int i = _pages.FindIndex(p => p.Key == _lastPageKey);
        return i < 0 ? 0 : i;
    }

    private void SwitchTo(string key)
    {
        LoadPages(); // une roue Stream Deck a pu arriver entre-temps
        int i = _pages.FindIndex(p => p.Key == key);
        if (i >= 0) ShowPage(i);
        else Notify?.Invoke("WheelConvert", "Cette roue n'existe plus.", true);
    }

    private void ShowPage(int index)
    {
        _dwellTimer.Stop();
        _dwellIndex = -1;

        if (_pages.Count == 0)
        {
            ShowEntries(new List<WheelEntry>(), "Actions", "Aucune roue");
            return;
        }

        _pageIndex = ((index % _pages.Count) + _pages.Count) % _pages.Count; // boucle
        var page = _pages[_pageIndex];
        _lastPageKey = page.Key;

        var entries = page.Build();
        string title = string.IsNullOrWhiteSpace(page.Glyph) ? page.Name : $"{page.Glyph} {page.Name}";
        ShowEntries(entries, title, PageHint(entries.Count));

        if (_mode == Mode.Radial) SetHover(_wheel.HitTest(ScreenToLocal(_lastX, _lastY)));
    }

    private string PageHint(int count)
    {
        string action = count == 0 ? "Roue vide" : _mode == Mode.Hotkey ? "Clic · Échap" : "Relâcher pour lancer";
        if (_pages.Count <= 1) return action;

        // Points de page : ● pour la roue en cours (au-delà de 7, simple compteur)
        string dots = _pages.Count <= 7
            ? string.Concat(Enumerable.Range(0, _pages.Count).Select(i => i == _pageIndex ? "●" : "○"))
            : $"{_pageIndex + 1}/{_pages.Count}";
        return $"{dots}\n{action}\nmolette : changer";
    }

    /// Survol + temporisation sur un secteur « lien » : bascule de roue sans relâcher.
    private void SetHover(int index)
    {
        _wheel.SetHover(index);
        if (index == _dwellIndex) return;

        _dwellIndex = index;
        _dwellTimer.Stop();
        if (Pick(index)?.SwitchTo is not null) _dwellTimer.Start();
    }

    private void OnDwell()
    {
        _dwellTimer.Stop();
        if (_mode is not (Mode.Radial or Mode.Hotkey)) return;
        if (Pick(_dwellIndex)?.SwitchTo is { } key) SwitchTo(key);
    }

    // ================= Clavier / focus =================

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_mode == Mode.Hotkey) HideWheel();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                HideWheel();
                break;
            case Key.Right or Key.Down or Key.Tab or Key.PageDown:
                ShowPage(_pageIndex + 1);
                break;
            case Key.Left or Key.Up or Key.PageUp:
                ShowPage(_pageIndex - 1);
                break;
            case >= Key.D1 and <= Key.D9:
                ShowPage(e.Key - Key.D1);
                break;
            case >= Key.NumPad1 and <= Key.NumPad9:
                ShowPage(e.Key - Key.NumPad1);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    public void UpdateHotkey()
    {
        UnregisterHotKey(_hwnd, HotkeyId);
        if (AppSettings.Current.HotkeyEnabled &&
            !RegisterHotKey(_hwnd, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_SPACE))
        {
            Notify?.Invoke("WheelConvert", "Ctrl+Alt+Espace est déjà utilisé par une autre application.", true);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            ShowQuickWheelAtCursor();
            handled = true;
        }
        return IntPtr.Zero;
    }

    // ================= Exécution =================

    private async void Execute(WheelEntry entry)
    {
        try
        {
            if (entry.NeedsForeground && _prevForeground != IntPtr.Zero)
            {
                SetForegroundWindow(_prevForeground);
                await Task.Delay(150);
            }
            if (entry.StartMessage is { } start) Notify?.Invoke("WheelConvert", start, false);

            string? message = await entry.Run();
            if (!string.IsNullOrEmpty(message)) Notify?.Invoke("WheelConvert", message, false);
        }
        catch (Exception ex)
        {
            Log.Write($"Erreur « {entry.Label} » : {ex}");
            Notify?.Invoke("WheelConvert — erreur", ex.Message, true);
        }
    }

    private WheelEntry? Pick(int index) => index >= 0 && index < _entries.Count ? _entries[index] : null;

    private void ShowEntries(List<WheelEntry> entries, string title, string hint)
    {
        _entries = entries;
        _wheel.SetItems(entries, title, hint);
    }

    // ================= Placement (pixels physiques, multi-écrans) =================

    private void PlaceAt(int x, int y, bool activate)
    {
        _px = Math.Max(1, (int)Math.Round(WheelControl.Diameter * GetDpiAt(x, y) / 96.0));
        _left = x - _px / 2;
        _top = y - _px / 2;

        SetWindowPos(_hwnd, HWND_TOPMOST, _left, _top, _px, _px, SWP_NOACTIVATE);
        if (!IsVisible) Show();
        SetWindowPos(_hwnd, HWND_TOPMOST, _left, _top, _px, _px,
            SWP_SHOWWINDOW | (activate ? 0u : SWP_NOACTIVATE));
    }

    private Point ScreenToLocal(int x, int y) =>
        new((x - _left) * WheelControl.Diameter / _px, (y - _top) * WheelControl.Diameter / _px);

    private static uint GetDpiAt(int x, int y)
    {
        var monitor = MonitorFromPoint(new POINT { X = x, Y = y }, 2 /* MONITOR_DEFAULTTONEAREST */);
        return GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi : 96;
    }

    // ================= Win32 =================

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_NOREPEAT = 0x4000, VK_SPACE = 0x20;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
}
