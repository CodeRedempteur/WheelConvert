using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using WheelConvert.Core;

namespace WheelConvert.Converters;

/// <summary>Actions valables pour n'importe quel fichier ou dossier.</summary>
public static class FileTools
{
    public static string? Zip(string[] items)
    {
        string first = items[0];
        string baseName = items.Length == 1 ? first : Path.Combine(Path.GetDirectoryName(first) ?? ".", "Archive");
        string output = OutputPaths.For(baseName, ".zip");

        try
        {
            using var zip = ZipFile.Open(output, ZipArchiveMode.Create);
            foreach (var item in items)
            {
                if (Directory.Exists(item))
                {
                    string root = Path.GetFileName(item);
                    foreach (var file in Directory.EnumerateFiles(item, "*", SearchOption.AllDirectories))
                    {
                        string entry = Path.Combine(root, Path.GetRelativePath(item, file)).Replace('\\', '/');
                        zip.CreateEntryFromFile(file, entry, CompressionLevel.Optimal);
                    }
                }
                else if (File.Exists(item))
                {
                    zip.CreateEntryFromFile(item, Path.GetFileName(item), CompressionLevel.Optimal);
                }
            }
        }
        catch
        {
            OutputPaths.TryDelete(output);
            throw;
        }
        ExplorerReveal.Show(output);
        return $"Archive créée : {Path.GetFileName(output)}";
    }

    public static string? CopyPaths(string[] items)
    {
        System.Windows.Clipboard.SetText(string.Join("\r\n", items));
        return $"{items.Length} chemin(s) copié(s)";
    }

    public static async Task<string?> CopyHashesAsync(string[] items)
    {
        var files = items.Where(File.Exists).ToArray();
        string text = await Task.Run(() =>
        {
            var sb = new StringBuilder();
            foreach (var f in files)
            {
                using var fs = File.OpenRead(f);
                string hash = System.Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
                sb.AppendLine($"{hash}  {Path.GetFileName(f)}");
            }
            return sb.ToString();
        });
        System.Windows.Clipboard.SetText(text);
        return $"SHA-256 de {files.Length} fichier(s) copié(s)";
    }
}
