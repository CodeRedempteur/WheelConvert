using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace WheelConvert.Core;

/// <summary>
/// Hooks souris + clavier bas niveau.
///  • Roue de fichiers : on commence un glisser NORMAL (sans Shift, donc sans sélection
///    par plage dans l'Explorateur), puis on appuie sur Shift pendant le glisser.
///  • Roue d'actions : Shift + clic droit maintenu, n'importe où (navigateur compris).
///    Le clic droit est absorbé pour ne pas ouvrir le menu contextuel.
/// Les événements sont relayés sur le thread UI.
/// </summary>
internal sealed class MouseHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    private const int VK_SHIFT = 0x10, VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1;
    private const int DragThreshold = 8;

    /// Roue de fichiers (glisser gauche + Shift).
    public event Action<int, int>? DragStarted, ButtonUp;
    /// Roue d'actions (Shift + clic droit maintenu).
    public event Action<int, int>? QuickDown, QuickUp;
    /// Mouvements pendant une session.
    public event Action<int, int>? Moved;

    public bool Enabled { get; set; } = true;

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly HookProc _mouseProc, _keyProc; // références gardées pour le GC
    private readonly IntPtr _mouseHook, _keyHook;

    private bool _down, _dragging, _shiftAtDown, _tracking, _quick;
    private bool? _fromExplorer;
    private POINT _start, _last;

    public MouseHook()
    {
        _mouseProc = MouseCallback;
        _keyProc = KeyCallback;
        IntPtr module = GetModuleHandle(null);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
        _keyHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyProc, module, 0);
        if (_mouseHook == IntPtr.Zero)
            throw new InvalidOperationException("Impossible d'installer le hook souris.");
    }

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Enabled)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            _last = data.pt;
            int x = data.pt.X, y = data.pt.Y;

            switch ((int)wParam)
            {
                // ---------- Roue d'actions : Shift + clic droit ----------
                case WM_RBUTTONDOWN when !_down && IsShiftDown() && AppSettings.Current.QuickWheelOnEmptyDrag:
                    _quick = true;
                    Post(QuickDown, x, y);
                    return (IntPtr)1; // absorbé : pas de menu contextuel

                case WM_RBUTTONUP when _quick:
                    _quick = false;
                    Post(QuickUp, x, y);
                    return (IntPtr)1;

                // ---------- Roue de fichiers : glisser puis Shift ----------
                case WM_LBUTTONDOWN:
                    _down = true;
                    _dragging = false;
                    _tracking = false;
                    _fromExplorer = null;
                    _start = data.pt;
                    // Shift déjà enfoncé = sélection par plage : on ignore tout ce glisser
                    _shiftAtDown = IsShiftDown();
                    break;

                case WM_MOUSEMOVE:
                    if (_quick || _tracking)
                    {
                        Post(Moved, x, y);
                    }
                    else if (_down)
                    {
                        if (!_dragging)
                        {
                            int dx = x - _start.X, dy = y - _start.Y;
                            _dragging = dx * dx + dy * dy >= DragThreshold * DragThreshold;
                        }
                        if (_dragging && IsShiftDown()) TryStartFileWheel();
                    }
                    break;

                case WM_LBUTTONUP:
                    _down = _dragging = false;
                    if (_tracking)
                    {
                        _tracking = false;
                        Post(ButtonUp, x, y);
                    }
                    break;
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    // Shift pressé pendant le glisser, même sans bouger la souris
    private IntPtr KeyCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Enabled && _down && _dragging && !_tracking)
        {
            int msg = (int)wParam;
            int vk = Marshal.ReadInt32(lParam);
            if ((msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) && vk is VK_SHIFT or VK_LSHIFT or VK_RSHIFT)
                TryStartFileWheel();
        }
        return CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    private void TryStartFileWheel()
    {
        if (_shiftAtDown || _tracking) return;
        _fromExplorer ??= !AppSettings.Current.ExplorerOnly || IsExplorerAt(_start);
        if (_fromExplorer != true) return;
        _tracking = true;
        Post(DragStarted, _last.X, _last.Y);
    }

    // Le hook doit rendre la main vite : tout le travail se fait sur le thread UI
    private void Post(Action<int, int>? handler, int x, int y)
    {
        if (handler is not null) _dispatcher.InvokeAsync(() => handler(x, y));
    }

    private static bool IsShiftDown() => (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;

    private static bool IsExplorerAt(POINT pt)
    {
        try
        {
            var hwnd = WindowFromPoint(pt);
            GetWindowThreadProcessId(hwnd, out uint pid);
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public void Dispose()
    {
        UnhookWindowsHookEx(_mouseHook);
        if (_keyHook != IntPtr.Zero) UnhookWindowsHookEx(_keyHook);
    }

    // ---- Win32 ----
    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData, flags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT pt);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
}