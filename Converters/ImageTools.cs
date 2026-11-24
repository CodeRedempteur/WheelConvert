using System;
using System.IO;
using ImageMagick;
using WheelConvert.Core;

namespace WheelConvert.Converters;

public enum ImageTarget { Png, Jpg, Webp }

public static class ImageTools
{
    public static string Convert(string source, ImageTarget target)
    {
        var s = AppSettings.Current;
        using var img = new MagickImage(source); // 1re image si GIF animé / multi-pages
        img.AutoOrient(); // rotation EXIF (photos de téléphone)

        string ext;
        MagickFormat format;
        switch (target)
        {
            case ImageTarget.Jpg:
                ext = ".jpg";
                format = MagickFormat.Jpeg;
                img.Quality = (uint)Math.Clamp(s.JpegQuality, 1, 100);
                if (img.HasAlpha)
                {
                    img.BackgroundColor = MagickColors.White; // pas de transparence en JPG
                    img.Alpha(AlphaOption.Remove);
                }
                break;

            case ImageTarget.Webp:
                ext = ".webp";
                format = MagickFormat.WebP;
                img.Quality = (uint)Math.Clamp(s.WebpQuality, 1, 100);
                break;

            default:
                ext = ".png";
                format = MagickFormat.Png;
                break;
        }

        string output = OutputPaths.For(source, ext);
        img.Write(output, format);
        return output;
    }

    /// Icône Windows multi-tailles (256, 64, 48, 32, 16), carrée et transparente.
    public static string ToIcon(string source)
    {
        using var img = new MagickImage(source);
        img.AutoOrient();
        img.Alpha(AlphaOption.Set);

        using var icons = new MagickImageCollection();
        foreach (uint size in new uint[] { 256, 64, 48, 32, 16 })
        {
            var frame = img.Clone();
            frame.Resize(size, size);
            frame.Extent(size, size, Gravity.Center, MagickColors.Transparent);
            icons.Add(frame);
        }

        string output = OutputPaths.For(source, ".ico");
        icons.Write(output, MagickFormat.Icon);
        return output;
    }

    /// Toutes les images sélectionnées dans un seul PDF (une page par image).
    public static string? ToPdf(string[] files)
    {
        using var pages = new MagickImageCollection();
        foreach (var f in files)
        {
            var page = new MagickImage(f);
            page.AutoOrient();
            pages.Add(page);
        }

        string output = OutputPaths.For(files[0], ".pdf", files.Length > 1 ? "_fusion" : null);
        pages.Write(output, MagickFormat.Pdf);
        ExplorerReveal.Show(output);
        return $"PDF créé ({files.Length} page(s)) : {Path.GetFileName(output)}";
    }

    /// Réduit l'image de moitié en gardant son format.
    public static string Half(string source)
    {
        using var img = new MagickImage(source);
        img.AutoOrient();
        img.Resize(new Percentage(50));

        string ext = Path.GetExtension(source).ToLowerInvariant();
        if (ext is ".heic" or ".heif" or ".psd" or ".jxl") ext = ".jpg"; // formats peu pratiques en sortie

        string output = OutputPaths.For(source, ext, "_50");
        img.Write(output);
        return output;
    }
}
