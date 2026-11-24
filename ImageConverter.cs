using System;
using System.Collections.Generic;
using System.IO;
using ImageMagick;

namespace WheelConvert;

internal static class ImageConverter
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jfif", ".png", ".webp", ".avif", ".heic", ".heif",
        ".bmp", ".gif", ".tif", ".tiff", ".ico", ".tga", ".jxl"
    };

    public static bool IsImage(string path) =>
        File.Exists(path) && Extensions.Contains(Path.GetExtension(path));

    /// Convertit l'image à côté de l'original, sans jamais écraser un fichier existant.
    public static string Convert(string source, string format)
    {
        using var img = new MagickImage(source); // 1re image si GIF animé / multi-pages
        img.AutoOrient(); // applique la rotation EXIF (photos de téléphone)

        string ext;
        MagickFormat target;

        switch (format)
        {
            case "JPG":
                ext = ".jpg";
                target = MagickFormat.Jpeg;
                img.Quality = 92;
                if (img.HasAlpha)
                {
                    img.BackgroundColor = MagickColors.White; // pas de transparence en JPG
                    img.Alpha(AlphaOption.Remove);
                }
                break;

            case "WEBP":
                ext = ".webp";
                target = MagickFormat.WebP;
                img.Quality = 90;
                break;

            case "ICO":
                ext = ".ico";
                target = MagickFormat.Icon;
                img.Resize(new MagickGeometry(256, 256) { Greater = true }); // max 256 px
                break;

            default:
                ext = ".png";
                target = MagickFormat.Png;
                break;
        }

        string output = UniquePath(Path.ChangeExtension(source, ext));
        img.Write(output, target);
        return output;
    }

    private static string UniquePath(string path)
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
}
