using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace WheelConvert.Core;

/// <summary>
/// Ouvre le dossier des fichiers créés et les sélectionne dans l'Explorateur. Réutilise une fenêtre
/// déjà ouverte sur ce dossier au lieu d'en ouvrir une nouvelle (SHOpenFolderAndSelectItems).
/// </summary>
public static class ExplorerReveal
{
    /// Montre les fichiers créés (si l'option est active). Sans effet si la liste est vide.
    public static void Show(IEnumerable<string> createdFiles)
    {
        if (!AppSettings.Current.RevealAfterConvert) return;
        var files = createdFiles.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0) return;

        // Appels Shell sur le thread UI (STA) : les conversions tournent en arrière-plan
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Reveal(files);
        else dispatcher.InvokeAsync(() => Reveal(files));
    }

    public static void Show(string createdFile) => Show(new[] { createdFile });

    private static void Reveal(List<string> files)
    {
        // Un dossier par fenêtre (en pratique un seul : à côté de l'original ou dans le sous-dossier « Converti »)
        foreach (var group in files.GroupBy(f => Path.GetDirectoryName(f) ?? "", StringComparer.OrdinalIgnoreCase).Take(3))
        {
            try
            {
                SelectInFolder(group.Key, group.ToList());
            }
            catch (Exception ex)
            {
                Log.Write($"Sélection dans l'Explorateur impossible ({ex.Message}) → explorer /select");
                Process.Start("explorer.exe", $"/select,\"{group.First()}\"");
            }
        }
    }

    private static void SelectInFolder(string folder, List<string> files)
    {
        IntPtr folderPidl = IntPtr.Zero;
        var itemPidls = new List<IntPtr>();
        try
        {
            Check(SHParseDisplayName(folder, IntPtr.Zero, out folderPidl, 0, out _));
            foreach (var f in files)
            {
                if (SHParseDisplayName(f, IntPtr.Zero, out var pidl, 0, out _) == 0 && pidl != IntPtr.Zero)
                    itemPidls.Add(pidl);
            }
            if (itemPidls.Count == 0) throw new IOException("aucun fichier à sélectionner");
            Check(SHOpenFolderAndSelectItems(folderPidl, (uint)itemPidls.Count, itemPidls.ToArray(), 0));
        }
        finally
        {
            foreach (var p in itemPidls) CoTaskMemFree(p);
            if (folderPidl != IntPtr.Zero) CoTaskMemFree(folderPidl);
        }
    }

    private static void Check(int hr)
    {
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint count,
        [MarshalAs(UnmanagedType.LPArray)] IntPtr[] items, uint flags);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);
}
