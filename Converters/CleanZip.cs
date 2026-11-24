using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using WheelConvert.Core;

namespace WheelConvert.Converters;

/// <summary>
/// « ZIP propre » : archive un dossier de projet sans ce qui se régénère ou pèse lourd — bin, obj, .vs, .git,
/// node_modules… à n'importe quelle profondeur (src/Projet1/bin, src/Projet2/obj…), plus les fichiers temporaires
/// et les fichiers trop gros. Les listes sont réglables dans le designer (onglet Général).
/// </summary>
public static class CleanZip
{
    public const string DefaultFolders =
        "bin; obj; .vs; .idea; .git; .svn; .hg; node_modules; packages; TestResults; AppPackages; " +
        "BundleArtifacts; __pycache__; .venv; venv; .gradle; .angular; .next; .nuxt; .parcel-cache; .cache; dist; " +
        "Debug; Release; x64; x86; ARM64";

    public const string DefaultFiles =
        "*.user; *.suo; *.tmp; *.log; *.cache; *.pdb; *.ilk; *.obj; *.vsidx; *.nupkg; *.snupkg; *.db-shm; *.db-wal; " +
        "Thumbs.db; .DS_Store; desktop.ini";

    private sealed class Stats
    {
        public int Files;
        public long Bytes;
        public int SkippedFolders;
        public int SkippedFiles;
        public int TooBig;
        public int Locked;
        public readonly SortedSet<string> FolderNames = new(StringComparer.OrdinalIgnoreCase);
    }

    public static string? Run(string[] items)
    {
        var s = AppSettings.Current;
        var folders = new HashSet<string>(Split(s.CleanZipFolders ?? DefaultFolders), StringComparer.OrdinalIgnoreCase);
        var filePatterns = Split(s.CleanZipFiles ?? DefaultFiles).Select(GlobToRegex).ToList();
        long maxBytes = s.CleanZipMaxFileMb <= 0 ? long.MaxValue : s.CleanZipMaxFileMb * 1024L * 1024L;

        // Nom : « Dossier.zip » à côté du dossier (ou « Archive.zip » pour une sélection multiple)
        string first = items[0].TrimEnd('\\', '/');
        string parent = Path.GetDirectoryName(first) ?? ".";
        string name = items.Length == 1 ? Path.GetFileName(first) : "Archive";
        string output = OutputPaths.Unique(Path.Combine(parent, name + ".zip"));

        var stats = new Stats();
        try
        {
            using var zip = ZipFile.Open(output, ZipArchiveMode.Create);
            foreach (var item in items)
            {
                string path = item.TrimEnd('\\', '/');
                if (Directory.Exists(path))
                    AddFolder(zip, new DirectoryInfo(path), Path.GetFileName(path), output, folders, filePatterns, maxBytes, stats);
                else if (File.Exists(path))
                    AddFile(zip, new FileInfo(path), Path.GetFileName(path), output, filePatterns, maxBytes, stats);
            }
        }
        catch
        {
            OutputPaths.TryDelete(output);
            throw;
        }

        if (stats.Files == 0)
        {
            OutputPaths.TryDelete(output);
            throw new InvalidOperationException("Rien à archiver : tout le contenu est exclu.");
        }

        Log.Write($"ZIP propre {Path.GetFileName(output)} : {stats.Files} fichiers, {stats.SkippedFolders} dossiers exclus " +
                  $"({string.Join(", ", stats.FolderNames)}), {stats.SkippedFiles} fichiers exclus, {stats.TooBig} trop gros, {stats.Locked} verrouillés");
        ExplorerReveal.Show(output);

        string zipSize = Size(new FileInfo(output).Length);
        var details = new List<string>();
        if (stats.SkippedFolders > 0)
            details.Add($"{stats.SkippedFolders} dossier(s) exclu(s) ({string.Join(", ", stats.FolderNames.Take(5))}{(stats.FolderNames.Count > 5 ? "…" : "")})");
        if (stats.TooBig > 0) details.Add($"{stats.TooBig} fichier(s) > {s.CleanZipMaxFileMb} Mo ignoré(s)");
        if (stats.Locked > 0) details.Add($"{stats.Locked} fichier(s) verrouillé(s) ignoré(s)");

        return $"{Path.GetFileName(output)} — {stats.Files} fichier(s), {zipSize}" +
               (details.Count > 0 ? "\n" + string.Join(" · ", details) : "");
    }

    private static void AddFolder(ZipArchive zip, DirectoryInfo dir, string entryRoot, string output,
        HashSet<string> excludedFolders, List<Regex> filePatterns, long maxBytes, Stats stats)
    {
        FileSystemInfo[] children;
        try { children = dir.GetFileSystemInfos(); }
        catch (UnauthorizedAccessException) { return; }

        foreach (var child in children.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            string entry = entryRoot + "/" + child.Name;

            if (child is DirectoryInfo sub)
            {
                // Exclu à toute profondeur (src/Projet/bin…) ; jonctions/liens ignorés (évite les boucles)
                if (excludedFolders.Contains(sub.Name) || sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    stats.SkippedFolders++;
                    stats.FolderNames.Add(sub.Name);
                    continue;
                }
                AddFolder(zip, sub, entry, output, excludedFolders, filePatterns, maxBytes, stats);
            }
            else if (child is FileInfo file)
            {
                if (file.FullName.Equals(output, StringComparison.OrdinalIgnoreCase)) continue; // l'archive elle-même
                AddFile(zip, file, entry, output, filePatterns, maxBytes, stats);
            }
        }
    }

    private static void AddFile(ZipArchive zip, FileInfo file, string entry, string output,
        List<Regex> filePatterns, long maxBytes, Stats stats)
    {
        if (filePatterns.Any(p => p.IsMatch(file.Name)) || IsBackupTemp(file.Name))
        {
            stats.SkippedFiles++;
            return;
        }
        if (file.Length > maxBytes)
        {
            stats.TooBig++;
            return;
        }

        try
        {
            // Lecture partagée : un fichier ouvert dans Visual Studio reste archivable
            using var source = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var zipEntry = zip.CreateEntry(entry, CompressionLevel.Optimal);
            if (file.LastWriteTime.Year is >= 1980 and <= 2107) zipEntry.LastWriteTime = file.LastWriteTime; // limites du format ZIP
            using var target = zipEntry.Open();
            source.CopyTo(target);
            stats.Files++;
            stats.Bytes += file.Length;
        }
        catch (IOException)
        {
            stats.Locked++;
        }
        catch (UnauthorizedAccessException)
        {
            stats.Locked++;
        }
    }

    /// « WheelConvert.csproj.Backup (1).tmp » et consorts créés par Visual Studio.
    private static bool IsBackupTemp(string name) =>
        name.Contains(".Backup", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> Split(string list) =>
        list.Split(new[] { ';', ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Trim('/', '\\'))
            .Where(x => x.Length > 0);

    private static Regex GlobToRegex(string glob) =>
        new("^" + Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string Size(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.0} Go",
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):0.0} Mo",
        >= 1024 => $"{bytes / 1024.0:0} Ko",
        _ => $"{bytes} o"
    };
}
