using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace WheelConvert.Core;

/// <summary>
/// Trouve le(s) fichier(s) sous le curseur dans l'Explorateur ou sur le bureau.
///  • Curseur sur un fichier déjà sélectionné → toute la sélection.
///  • Curseur sur un autre fichier            → ce fichier seul.
///  • Curseur dans le vide / ailleurs         → null (roue d'actions).
/// </summary>
public static class ExplorerProbe
{
    private enum ViewKind { None, Explorer, Desktop }

    /// Vérification rapide (thread UI) : le curseur est-il sur une fenêtre de l'Explorateur ou sur le bureau ?
    public static bool IsShellViewAt(int x, int y) => KindAt(x, y, out _) != ViewKind.None;

    public static async Task<string[]?> GetFilesAtAsync(int x, int y)
    {
        var kind = KindAt(x, y, out IntPtr root);
        if (kind == ViewKind.None) return null;

        var work = RunSta(() => Resolve(x, y, kind, root));
        var done = await Task.WhenAny(work, Task.Delay(1500));
        if (done != work) return null;
        try { return await work; } catch { return null; }
    }

    // ================= Résolution (thread STA dédié) =================

    private static string[]? Resolve(int x, int y, ViewKind kind, IntPtr root)
    {
        string? name = ItemNameAt(x, y);
        if (string.IsNullOrEmpty(name)) return null; // dans le vide

        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        dynamic? view = kind == ViewKind.Desktop ? DesktopView(shell) : ExplorerView(shell, root);

        var folders = new List<string>();
        var selection = new List<(string Name, string Path)>();

        if (view is not null)
        {
            try
            {
                string folder = (string)view.Folder.Self.Path;
                if (Directory.Exists(folder)) folders.Add(folder);
            }
            catch { }
            try
            {
                foreach (dynamic item in view.SelectedItems())
                    selection.Add(((string)item.Name, (string)item.Path));
            }
            catch { }
        }

        if (kind == ViewKind.Desktop)
        {
            folders.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            folders.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
        }

        // Fichier sous le curseur faisant partie de la sélection → toute la sélection
        if (selection.Any(s => s.Name == name))
        {
            var paths = selection.Select(s => s.Path).Where(Exists).ToArray();
            if (paths.Length > 0) return paths;
        }

        // Sinon, le fichier sous le curseur uniquement
        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string? path = FindInFolder(shell, folder, name);
            if (path is not null) return new[] { path };
        }
        return null;
    }

    /// Nom affiché de l'élément de liste sous le curseur (UI Automation).
    private static string? ItemNameAt(int x, int y)
    {
        try
        {
            var el = AutomationElement.FromPoint(new System.Windows.Point(x, y));
            var walker = TreeWalker.ControlViewWalker;
            for (int depth = 0; el is not null && depth < 6; depth++)
            {
                var type = el.Current.ControlType;
                if (type == ControlType.ListItem || type == ControlType.DataItem) return el.Current.Name;
                if (type == ControlType.List || type == ControlType.Window) return null; // zone vide de la liste
                el = walker.GetParent(el);
            }
        }
        catch { }
        return null;
    }

    private static dynamic? ExplorerView(dynamic shell, IntPtr root)
    {
        try
        {
            var candidates = new List<dynamic>();
            foreach (dynamic w in shell.Windows())
            {
                try
                {
                    if (Convert.ToInt64(w.HWND) == root.ToInt64()) candidates.Add(w);
                }
                catch { }
            }
            if (candidates.Count == 0) return null;
            if (candidates.Count == 1) return candidates[0].Document;

            // Onglets Windows 11 : même fenêtre, on prend l'onglet dont le nom correspond au titre
            string title = WindowTitle(root);
            foreach (var c in candidates)
            {
                try
                {
                    string loc = (string)c.LocationName;
                    if (!string.IsNullOrEmpty(loc) && title.StartsWith(loc, StringComparison.OrdinalIgnoreCase))
                        return c.Document;
                }
                catch { }
            }
            return candidates[0].Document;
        }
        catch { return null; }
    }

    private static dynamic? DesktopView(dynamic shell)
    {
        try
        {
            dynamic windows = shell.Windows();
            object loc = 0;       // CSIDL_DESKTOP
            object? empty = null;
            int hwnd;
            dynamic browser = windows.FindWindowSW(ref loc, ref empty, 8 /* SWC_DESKTOP */, out hwnd, 1 /* SWFO_NEEDDISPATCH */);
            return browser?.Document;
        }
        catch { return null; }
    }

    private static string? FindInFolder(dynamic shell, string folder, string name)
    {
        try
        {
            dynamic? ns = shell.NameSpace(folder);
            if (ns is null) return null;

            dynamic? direct = ns.ParseName(name); // extensions affichées
            if (direct is not null && (string)direct.Name == name && Exists((string)direct.Path))
                return (string)direct.Path;

            foreach (dynamic item in ns.Items()) // extensions masquées
            {
                if ((string)item.Name != name) continue;
                string path = (string)item.Path;
                if (Exists(path)) return path;
            }
        }
        catch { }
        return null;
    }

    // ================= Utilitaires =================

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static ViewKind KindAt(int x, int y, out IntPtr root)
    {
        root = GetAncestor(WindowFromPoint(new POINT { X = x, Y = y }), 2 /* GA_ROOT */);
        string cls = ClassName(root);
        if (cls == "CabinetWClass") return ViewKind.Explorer;
        if (cls is "Progman" or "WorkerW") return ViewKind.Desktop;
        return ViewKind.None;
    }

    private static Task<T> RunSta<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    private static string WindowTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        return GetWindowText(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT pt);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
}
