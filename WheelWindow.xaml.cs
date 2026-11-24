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
///  • Glisser des fichiers puis Shift → roue de conversion adaptée au type.
///  • Shift + clic droit maintenu     → roue d'actions rapides (relâcher sur une action).
///  • Ctrl + Alt + Espace             → roue d'actions rapides au curseur (clic).
/// </summary>
public partial class WheelWindow : Window
{
    private enum Mode { Hidden, Pending, Quick, Files, Hotkey }

    private const int HotkeyId = 0x5743;

    private readonly WheelControl _wheel = new();
    private readonly DispatcherTimer _pendingTimer;
    private readonly IntPtr _hwnd;

    private Mode _mode = Mode.Hidden;
    private List<WheelEntry> _entries = new();
    private string? _dragKey;
    private int _left, _top, _px = 1, _session;
    private IntPtr _prevForeground;

    public event Action<string, string, bool>? Notify;

    public bool Paused { get; set; }

    public WheelWindow()
    {
        InitializeComponent();
        Host.Child = _wheel;

        _wheel.MouseMove += (_, e) =>
        {
            if (_mode == Mode.Hotkey) _wheel.SetHover(_wheel.HitTest(e.GetPosition(_wheel)));
        };
        _wheel.MouseLeftButtonUp += (_, e) =>
        {
            if (_mode != Mode.Hotkey) return;
            var entry = Pick(_wheel.HitTest(e.GetPosition(_wheel)));
            HideWheel();
            if (entry is not null) Execute(entry);
        };

        // Si aucun fichier n'arrive juste après l'apparition, ce n'était pas un glisser de fichiers
        _pendingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _pendingTimer.Tick += (_, _) =>
        {
            _pendingTimer.Stop();
            if (_mode == Mode.Pending) HideWheel();
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

    // ================= Sessions =================

    public void BeginDragSession(int x, int y)
    {
        if (Paused) return;
        _session++;
        _prevForeground = IntPtr.Zero;
        _dragKey = null;
        _mode = Mode.Pending;
        ShowEntries(new List<WheelEntry>(), "…", "");
        PlaceAt(x, y, activate: false);
        _pendingTimer.Stop();
        _pendingTimer.Start();
    }

    public void ShowQuickWheelAtCursor()
    {
        if (Paused) return;
        _session++;
        _pendingTimer.Stop();
        _prevForeground = GetForegroundWindow();
        if (_prevForeground == _hwnd) _prevForeground = IntPtr.Zero;
        GetCursorPos(out var p);

        _mode = Mode.Hotkey;
        var quick = QuickEntries();
        ShowEntries(quick, "Actions", quick.Count == 0 ? "Aucune action" : "Clic · Échap");
        PlaceAt(p.X, p.Y, activate: true);
        Activate();
        Focus();
    }

    /// Shift + clic droit maintenu : roue d'actions, fonctionne dans toutes les fenêtres.
    public void BeginQuickSession(int x, int y)
    {
        if (Paused) return;
        _session++;
        _pendingTimer.Stop();
        _prevForeground = GetForegroundWindow(); // pour les raccourcis clavier / collage
        if (_prevForeground == _hwnd) _prevForeground = IntPtr.Zero;

        _mode = Mode.Quick;
        var quick = QuickEntries();
        ShowEntries(quick, "Actions", quick.Count == 0 ? "Aucune action" : "Relâcher pour lancer");
        PlaceAt(x, y, activate: false);
    }

    public void OnQuickUp(int x, int y)
    {
        if (_mode != Mode.Quick) return;
        var entry = Pick(_wheel.HitTest(ScreenToLocal(x, y)));
        HideWheel();
        if (entry is not null) Execute(entry);
    }

    public void OnHookMove(int x, int y)
    {
        if (_mode == Mode.Quick) _wheel.SetHover(_wheel.HitTest(ScreenToLocal(x, y)));
    }

    public async void OnHookButtonUp(int x, int y)
    {
        switch (_mode)
        {
            case Mode.Pending:
                HideWheel();
                break;

            case Mode.Files:
                {
                    // Laisse le temps à l'événement Drop d'arriver
                    int session = _session;
                    await Task.Delay(450);
                    if (session == _session && _mode == Mode.Files) HideWheel();
                    break;
                }
        }
    }

    private void HideWheel()
    {
        _pendingTimer.Stop();
        _mode = Mode.Hidden;
        _wheel.SetHover(-1);
        Hide();
    }

    // ================= Glisser-déposer =================

    private bool TryEnterFiles(DragEventArgs e)
    {
        string[]? files = null;
        try { files = e.Data.GetData(DataFormats.FileDrop) as string[]; } catch { }
        if (files is null || files.Length == 0) return _mode == Mode.Files;

        string key = string.Join("|", files);
        if (_mode == Mode.Files && key == _dragKey) return true;

        _dragKey = key;
        _pendingTimer.Stop();
        _mode = Mode.Files;
        var (title, entries) = ConversionCatalog.For(files);
        ShowEntries(entries, title, "Relâcher sur un format");
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
        // Toujours « Copier » : avec Shift, l'Explorateur ferait sinon un déplacement
        e.Effects = Pick(index) is not null ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => _wheel.SetHover(-1);

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var entry = _mode == Mode.Files ? Pick(_wheel.HitTest(e.GetPosition(_wheel))) : null;
        e.Effects = entry is not null ? DragDropEffects.Copy : DragDropEffects.None;
        HideWheel();
        // Rend la main à l'Explorateur avant de travailler
        if (entry is not null) Dispatcher.InvokeAsync(() => Execute(entry));
    }

    // ================= Clavier / focus =================

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_mode == Mode.Hotkey) HideWheel();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideWheel();
            e.Handled = true;
        }
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
            Notify?.Invoke("WheelConvert — erreur", ex.Message, true);
        }
    }

    private static List<WheelEntry> QuickEntries() =>
        AppSettings.Current.QuickActions.Select(QuickActionRunner.ToEntry).ToList();

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