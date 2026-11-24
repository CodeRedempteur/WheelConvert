using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace WheelConvert.Core;

/// <summary>
/// Hooks souris + clavier bas niveau.
///  • Roue de conversion : pendant un glisser de fichiers (clic gauche maintenu, sans Shift au départ),
///    SECOUER la souris gauche-droite, ou appuyer sur Shift.
///  • Roue d'actions : Shift + clic droit maintenu, n'importe où (le menu contextuel est absorbé).
/// Les événements sont relayés sur le thread UI.
/// </summary>
internal sealed class MouseHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int VK_SHIFT = 0x10, VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1;

    private const int DragThreshold = 8;     // px avant de considérer un glisser
    private const int ShakeDistance = 30;    // px dans un sens avant un demi-tour
    private const int ShakeReversals = 3;    // demi-tours nécessaires
    private const uint ShakeWindowMs = 900;  // dans ce laps de temps

    /// Roue de conversion (glisser gauche).
    public event Action<int, int, string>? DragStarted;
    public event Action<int, int>? ButtonUp;
    /// Roue d'actions (Shift + clic droit).
    public event Action<int, int>? QuickDown, QuickUp;
    /// Mouvements pendant une session.
    public event Action<int, int>? Moved;
    /// Molette pendant Shift + clic droit maintenu : +1 (vers le bas = roue suivante) / -1.
    public event Action<int>? Scroll;

    public bool Enabled { get; set; } = true;

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly HookProc _mouseProc, _keyProc; // références gardées pour le GC
    private readonly IntPtr _mouseHook, _keyHook;

    // Glisser gauche
    private bool _down, _dragging, _shiftAtDown, _tracking;
    private POINT _start, _last;

    // Secousse
    private int _dir, _anchorX;
    private readonly List<uint> _reversals = new();

    // Clic droit
    private bool _quick;

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
            var s = AppSettings.Current;

            switch ((int)wParam)
            {
                // ---------- Roue d'actions : Shift + clic droit ----------
                case WM_RBUTTONDOWN when !_down && IsShiftDown() && s.QuickWheelOnEmptyDrag:
                    _quick = true;
                    Post(QuickDown, x, y);
                    return (IntPtr)1; // absorbé : pas de menu contextuel

                case WM_RBUTTONUP when _quick:
                    _quick = false;
                    Post(QuickUp, x, y);
                    return (IntPtr)1;

                // Molette pendant la roue d'actions : change de roue (absorbée, la page derrière ne défile pas)
                case WM_MOUSEWHEEL when _quick:
                    short delta = (short)((data.mouseData >> 16) & 0xFFFF);
                    int step = delta < 0 ? 1 : -1;
                    var scroll = Scroll;
                    if (scroll is not null && delta != 0) _dispatcher.InvokeAsync(() => scroll(step));
                    return (IntPtr)1;

                // ---------- Roue de conversion : glisser gauche ----------
                case WM_LBUTTONDOWN:
                    _down = true;
                    _dragging = _tracking = false;
                    _start = data.pt;
                    _shiftAtDown = IsShiftDown(); // Shift au clic = sélection par plage : pas de Shift-trigger
                    _dir = 0;
                    _anchorX = x;
                    _reversals.Clear();
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
                        if (!_dragging) break;

                        if (s.ShakeToOpen && DetectShake(x, data.time)) Start("secousse");
                        else if (s.ShiftDuringDrag && !_shiftAtDown && IsShiftDown()) Start("shift");
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
        if (nCode >= 0 && Enabled && _down && _dragging && !_tracking && !_shiftAtDown
            && AppSettings.Current.ShiftDuringDrag)
        {
            int msg = (int)wParam;
            int vk = Marshal.ReadInt32(lParam);
            if ((msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) && vk is VK_SHIFT or VK_LSHIFT or VK_RSHIFT)
                Start("shift");
        }
        return CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    /// Demi-tours horizontaux rapides (gauche-droite-gauche…).
    private bool DetectShake(int x, uint time)
    {
        if (_dir == 0)
        {
            if (Math.Abs(x - _anchorX) >= ShakeDistance)
            {
                _dir = Math.Sign(x - _anchorX);
                _anchorX = x;
            }
            return false;
        }

        if (Math.Sign(x - _anchorX) == _dir)
        {
            _anchorX = x; // on continue dans le même sens : nouvel extrême
            return false;
        }

        if (Math.Abs(x - _anchorX) < ShakeDistance) return false;

        // Demi-tour
        _dir = -_dir;
        _anchorX = x;
        _reversals.Add(time);
        _reversals.RemoveAll(t => time - t > ShakeWindowMs);
        return _reversals.Count >= ShakeReversals;
    }

    private void Start(string reason)
    {
        if (_tracking) return;
        _tracking = true;
        int x = _last.X, y = _last.Y;
        var handler = DragStarted;
        if (handler is not null) _dispatcher.InvokeAsync(() => handler(x, y, reason));
    }

    // Le hook doit rendre la main vite : tout le travail se fait sur le thread UI
    private void Post(Action<int, int>? handler, int x, int y)
    {
        if (handler is not null) _dispatcher.InvokeAsync(() => handler(x, y));
    }

    private static bool IsShiftDown() => (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;

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
}
