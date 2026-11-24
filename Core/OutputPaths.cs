using System.IO;

namespace WheelConvert.Core;

public static class OutputPaths
{
    /// Chemin de sortie selon les paramètres (à côté ou sous-dossier), jamais d'écrasement.
    public static string For(string source, string newExtension, string? suffix = null)
    {
        string dir = Path.GetDirectoryName(source) ?? ".";
        var s = AppSettings.Current;
        if (s.Output == OutputMode.SubFolder)
        {
            string sub = string.IsNullOrWhiteSpace(s.SubFolderName) ? "Converti" : s.SubFolderName.Trim();
            dir = Path.Combine(dir, sub);
            Directory.CreateDirectory(dir);
        }

        string name = Path.GetFileNameWithoutExtension(source) + (suffix ?? "");
        return Unique(Path.Combine(dir, name + newExtension));
    }

    public static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            string candidate = Path.Combine(dir, $"{name}_{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
