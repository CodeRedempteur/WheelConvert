using System;
using System.Collections.Generic;
using System.Diagnostics;
using Bitmap = System.Drawing.Bitmap;
using Graphics = System.Drawing.Graphics;
using Rectangle = System.Drawing.Rectangle;
using CopyPixelOperation = System.Drawing.CopyPixelOperation;
using ImageLockMode = System.Drawing.Imaging.ImageLockMode;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ImageMagick;
using WheelConvert.Core;
using ZXing;
using ZXing.Common;

namespace WheelConvert.Converters;

/// <summary>
/// Lecture de QR codes (et codes-barres courants) : dans des fichiers image, ou directement à l'écran.
/// Un lien http(s) est ouvert dans le navigateur ; tout autre contenu (texte, Wi-Fi, vCard…) est copié.
/// </summary>
public static class QrTools
{
    private const int MaxOpen = 5;      // au-delà, les liens sont seulement copiés
    private const uint MaxSide = 4000;  // les très grandes images sont réduites avant lecture

    private static readonly BarcodeFormat[] Formats =
    {
        BarcodeFormat.QR_CODE, BarcodeFormat.DATA_MATRIX, BarcodeFormat.AZTEC, BarcodeFormat.PDF_417,
        BarcodeFormat.EAN_13, BarcodeFormat.EAN_8, BarcodeFormat.CODE_128, BarcodeFormat.CODE_39, BarcodeFormat.UPC_A
    };

    // ================= Fichiers image =================

    /// Roue de conversion : lit les codes de toutes les images sélectionnées.
    public static async Task<string?> OpenFromFilesAsync(string[] files)
    {
        var texts = await Task.Run(() =>
        {
            var found = new List<string>();
            foreach (var f in files)
            {
                try { found.AddRange(DecodeFile(f)); }
                catch (Exception ex) { Log.Write($"QR illisible dans {Path.GetFileName(f)} : {ex.Message}"); }
            }
            return found;
        });
        return Handle(texts, files.Length == 1 ? Path.GetFileName(files[0]) : $"{files.Length} images");
    }

    private static IEnumerable<string> DecodeFile(string path)
    {
        using var img = new MagickImage(path); // 1re page / 1re image
        img.AutoOrient();
        if (img.Width > MaxSide || img.Height > MaxSide) img.Resize(new MagickGeometry(MaxSide, MaxSide));
        img.BackgroundColor = MagickColors.White;
        img.Alpha(AlphaOption.Remove); // fond transparent → blanc (sinon un QR noir sur transparent disparaît)
        img.Depth = 8;                 // 3 octets par pixel en RGB

        byte[] rgb = img.ToByteArray(MagickFormat.Rgb);
        return Decode(new RGBLuminanceSource(rgb, (int)img.Width, (int)img.Height, RGBLuminanceSource.BitmapFormat.RGB24));
    }

    // ================= Écran =================

    /// Action rapide : capture tous les écrans et lit les codes visibles.
    public static async Task<string?> ScanScreenAsync()
    {
        await Task.Delay(250); // laisse la roue disparaître de l'écran
        var texts = await Task.Run(() =>
        {
            var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
            using var bmp = new Bitmap(bounds.Width, bounds.Height, DrawingPixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            return Decode(SourceFrom(bmp)).ToList();
        });
        return Handle(texts, "l'écran");
    }

    private static LuminanceSource SourceFrom(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, DrawingPixelFormat.Format32bppArgb);
        try
        {
            // Copie ligne par ligne : le pas (stride) peut dépasser largeur × 4
            int rowBytes = bmp.Width * 4;
            var pixels = new byte[rowBytes * bmp.Height];
            for (int y = 0; y < bmp.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * rowBytes, rowBytes);
            return new RGBLuminanceSource(pixels, bmp.Width, bmp.Height, RGBLuminanceSource.BitmapFormat.BGRA32);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    // ================= Décodage / résultat =================

    private static IEnumerable<string> Decode(LuminanceSource source)
    {
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                TryInverted = true, // QR clair sur fond sombre
                PossibleFormats = Formats
            }
        };

        var results = reader.DecodeMultiple(source);
        if (results is { Length: > 0 }) return results.Select(r => r.Text).Where(t => !string.IsNullOrWhiteSpace(t));

        var single = reader.Decode(source);
        return single is null ? Array.Empty<string>() : new[] { single.Text };
    }

    /// Ouvre les liens, copie le reste. Appelé sur le thread UI (presse-papiers).
    private static string? Handle(List<string> texts, string where)
    {
        var distinct = texts.Select(t => NormalizeLink(t.Trim())).Where(t => t.Length > 0).Distinct().ToList();
        if (distinct.Count == 0)
            throw new InvalidOperationException($"Aucun QR code trouvé dans {where}.");

        Log.Write($"QR trouvé(s) dans {where} : {string.Join(" | ", distinct)}");

        var links = distinct.Where(IsWebLink).ToList();
        foreach (var url in links.Take(MaxOpen))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

        // Tout est copié (un par ligne) : pratique pour un texte, un code Wi-Fi ou les liens non ouverts
        try { System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, distinct)); } catch { }

        if (links.Count > 0 && links.Count == distinct.Count)
            return links.Count == 1 ? $"Lien ouvert : {Shorten(links[0])}" : $"{Math.Min(links.Count, MaxOpen)} lien(s) ouvert(s)";

        return links.Count > 0
            ? $"{links.Count} lien(s) ouvert(s), contenu copié : {Shorten(distinct.First(t => !IsWebLink(t)))}"
            : $"QR copié dans le presse-papiers : {Shorten(distinct[0])}";
    }

    /// « orionpospro.com/docs/… » ou « www.site.ch » sans http:// → lien https complet.
    private static string NormalizeLink(string text)
    {
        if (text.Length == 0 || text.Contains(' ') || text.Contains('\n') || IsWebLink(text)) return text;
        bool looksLikeDomain = System.Text.RegularExpressions.Regex.IsMatch(text,
            @"^(www\.)?[a-z0-9-]+(\.[a-z0-9-]+)*\.[a-z]{2,}(:\d+)?(/\S*)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return looksLikeDomain ? "https://" + text : text;
    }

    private static bool IsWebLink(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string Shorten(string text) => text.Length <= 80 ? text : text[..77] + "…";
}
