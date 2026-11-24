using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WheelConvert.Core;

namespace WheelConvert.Converters;

/// <summary>Construit la roue adaptée aux fichiers glissés.</summary>
public static class ConversionCatalog
{
    private const string Blue = "#2D7DFF", Red = "#E5484D", Purple = "#8E4EC6", Teal = "#12A594", Gray = "#5B5B66";

    public static (string Title, List<WheelEntry> Entries) For(string[] items)
    {
        // Type majoritaire de la sélection ; les conversions ne s'appliquent qu'aux fichiers de ce type
        var group = items
            .Select(p => (Path: p, Kind: FileKinds.Detect(p)))
            .Where(d => d.Kind != FileKind.Other)
            .GroupBy(d => d.Kind)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();

        FileKind kind = group?.Key ?? FileKind.Other;
        string[] files = group?.Select(d => d.Path).ToArray() ?? Array.Empty<string>();
        var list = new List<WheelEntry>();

        switch (kind)
        {
            case FileKind.Image:
                list.Add(Batch("PNG", "image", "🖼", Blue, files, f => ImageTools.Convert(f, ImageTarget.Png)));
                list.Add(Batch("JPG", "image", "📷", Blue, files, f => ImageTools.Convert(f, ImageTarget.Jpg)));
                list.Add(Batch("WEBP", "web", "🌐", Blue, files, f => ImageTools.Convert(f, ImageTarget.Webp)));
                list.Add(Batch("ICO", "icône", "🔷", Blue, files, ImageTools.ToIcon));
                list.Add(OneShot("PDF", files.Length > 1 ? "fusion" : "document", "📄", Blue, () => ImageTools.ToPdf(files)));
                list.Add(Batch("50 %", "réduire", "🗜", Blue, files, ImageTools.Half));
                // Lit le QR code de l'image : lien ouvert dans le navigateur, autre contenu copié
                list.Add(new WheelEntry
                {
                    Label = "QR", Sub = "ouvrir", Glyph = "🔳", Color = Blue,
                    Run = () => QrTools.OpenFromFilesAsync(files) // pas de Task.Run : le résultat revient sur le thread UI (presse-papiers)
                });
                break;

            case FileKind.Video:
                const string v = "Conversion vidéo en cours…";
                list.Add(Batch("MP4", "H.264", "🎬", Red, files, MediaTools.ToMp4, v));
                list.Add(Batch("MP3", "audio", "🎵", Red, files, MediaTools.ToMp3, v));
                list.Add(Batch("GIF", "animé", "🎞", Red, files, MediaTools.ToGif, v));
                list.Add(Batch("WEBM", "VP9", "🌐", Red, files, MediaTools.ToWebm, v));
                list.Add(Batch("720p", "compresser", "🗜", Red, files, MediaTools.Shrink720, v));
                list.Add(Batch("Image", "miniature", "🖼", Red, files, MediaTools.Thumbnail));
                break;

            case FileKind.Audio:
                const string a = "Conversion audio en cours…";
                list.Add(Batch("MP3", "192 kbps", "🎵", Purple, files, MediaTools.ToMp3, a));
                list.Add(Batch("WAV", "PCM", "🎚", Purple, files, MediaTools.ToWav, a));
                list.Add(Batch("FLAC", "sans perte", "💿", Purple, files, MediaTools.ToFlac, a));
                list.Add(Batch("M4A", "AAC", "🎧", Purple, files, MediaTools.ToM4a, a));
                list.Add(Batch("OGG", "Vorbis", "🔊", Purple, files, MediaTools.ToOgg, a));
                break;

            case FileKind.Model3D:
                list.Add(Batch("STL", "impression", "🧊", Teal, files, ModelTools.ToStl));
                list.Add(Batch("3MF", "impression", "🖨", Teal, files, ModelTools.To3mf));
                list.Add(Batch("OBJ", "maillage", "🔺", Teal, files, ModelTools.ToObj));
                list.Add(Batch("PLY", "maillage", "📐", Teal, files, ModelTools.ToPly));
                break;
        }

        // Dossier(s) sélectionné(s) : ZIP « propre » sans bin/obj/.vs/node_modules… (à toute profondeur)
        if (items.Any(Directory.Exists))
        {
            list.Add(new WheelEntry
            {
                Label = "ZIP propre", Sub = "sans bin/obj", Glyph = "🧹", Color = Teal,
                StartMessage = "Archivage du projet en cours…",
                Run = () => Task.Run(() => CleanZip.Run(items))
            });
        }

        // Actions communes (sur toute la sélection, dossiers compris)
        list.Add(new WheelEntry
        {
            Label = "ZIP", Sub = "archiver", Glyph = "📦", Color = Gray,
            Run = () => Task.Run(() => FileTools.Zip(items))
        });
        list.Add(new WheelEntry
        {
            Label = "Chemin", Sub = "copier", Glyph = "📋", Color = Gray,
            Run = () => Task.FromResult(FileTools.CopyPaths(items))
        });
        if (kind == FileKind.Other)
        {
            list.Add(new WheelEntry
            {
                Label = "SHA-256", Sub = "empreinte", Glyph = "🔑", Color = Gray,
                Run = () => FileTools.CopyHashesAsync(items)
            });
        }

        string title = kind switch
        {
            FileKind.Image => Count(files.Length, "image", "images"),
            FileKind.Video => Count(files.Length, "vidéo", "vidéos"),
            FileKind.Audio => Count(files.Length, "audio", "audios"),
            FileKind.Model3D => Count(files.Length, "modèle 3D", "modèles 3D"),
            _ => Count(items.Length, "élément", "éléments")
        };
        return (title, list);
    }

    private static string Count(int n, string one, string many) => $"{n} {(n > 1 ? many : one)}";

    private static WheelEntry Batch(string label, string sub, string glyph, string color,
        string[] files, Func<string, string> convert, string? start = null) => new()
    {
        Label = label, Sub = sub, Glyph = glyph, Color = color,
        StartMessage = start,
        Run = () => Task.Run(() => RunBatch(files, label, convert))
    };

    private static WheelEntry OneShot(string label, string sub, string glyph, string color, Func<string?> work) => new()
    {
        Label = label, Sub = sub, Glyph = glyph, Color = color,
        Run = () => Task.Run(work)
    };

    private static string? RunBatch(string[] files, string label, Func<string, string> convert)
    {
        int ok = 0;
        var errors = new List<string>();
        var created = new List<string>();
        foreach (var f in files)
        {
            try
            {
                created.Add(convert(f));
                ok++;
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(f)} : {ex.Message}");
            }
        }

        string msg = $"{ok}/{files.Length} fichier(s) → {label}";
        if (errors.Count > 0)
            msg += $"\n{errors[0]}" + (errors.Count > 1 ? $" (+{errors.Count - 1} autre(s))" : "");
        if (ok == 0) throw new InvalidOperationException(msg);
        ExplorerReveal.Show(created); // ouvre le dossier et sélectionne les nouveaux fichiers
        return msg;
    }
}
