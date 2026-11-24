using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using WheelConvert.Core;

namespace WheelConvert.Converters;

/// <summary>Conversions vidéo / audio via FFmpeg.</summary>
public static class MediaTools
{
    // ---- Vidéo ----
    public static string ToMp4(string s) => Encode(s, ".mp4", null,
        "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p",
        "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart");

    public static string ToGif(string s) => Encode(s, ".gif", null,
        "-vf", "fps=12,scale='min(480,iw)':-1:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse",
        "-loop", "0");

    public static string ToWebm(string s) => Encode(s, ".webm", null,
        "-c:v", "libvpx-vp9", "-crf", "33", "-b:v", "0", "-deadline", "good", "-cpu-used", "4", "-row-mt", "1",
        "-c:a", "libopus", "-b:a", "128k");

    public static string Shrink720(string s) => Encode(s, ".mp4", "_720p",
        "-vf", "scale=-2:'min(720,ih)'",
        "-c:v", "libx264", "-preset", "veryfast", "-crf", "28", "-pix_fmt", "yuv420p",
        "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart");

    public static string Thumbnail(string s) => Encode(s, ".png", "_miniature",
        "-vf", "thumbnail", "-frames:v", "1");

    // ---- Audio (depuis un fichier audio ou vidéo) ----
    public static string ToMp3(string s) => Encode(s, ".mp3", null, "-vn", "-c:a", "libmp3lame", "-b:a", "192k");
    public static string ToWav(string s) => Encode(s, ".wav", null, "-vn", "-c:a", "pcm_s16le");
    public static string ToFlac(string s) => Encode(s, ".flac", null, "-vn", "-c:a", "flac");
    public static string ToM4a(string s) => Encode(s, ".m4a", null, "-vn", "-c:a", "aac", "-b:a", "192k");
    public static string ToOgg(string s) => Encode(s, ".ogg", null, "-vn", "-c:a", "libvorbis", "-q:a", "5");

    private static string Encode(string source, string ext, string? suffix, params string[] codecArgs)
    {
        string ffmpeg = TryLocate() ?? throw new FileNotFoundException(
            "FFmpeg introuvable. Ouvre le designer (onglet Général) pour l'installer.");

        string output = OutputPaths.For(source, ext, suffix);

        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-i", source })
            psi.ArgumentList.Add(a);
        foreach (var a in codecArgs) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(output);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Impossible de lancer FFmpeg.");
        var errTask = p.StandardError.ReadToEndAsync();
        var outTask = p.StandardOutput.ReadToEndAsync();
        p.WaitForExit();
        string err = errTask.Result;
        _ = outTask.Result;

        if (p.ExitCode != 0)
        {
            OutputPaths.TryDelete(output);
            string? last = err.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
            throw new InvalidOperationException(string.IsNullOrEmpty(last) ? $"FFmpeg a échoué (code {p.ExitCode})." : last);
        }
        return output;
    }

    /// Cherche ffmpeg.exe : chemin choisi, dossier de l'app, %AppData%, liens winget, PATH, paquets winget.
    public static string? TryLocate(string? custom = null)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new List<string?>
        {
            custom,
            AppSettings.Current.FfmpegPath,
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(AppSettings.Folder, "ffmpeg.exe"),
            Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe")
        };
        foreach (var c in candidates)
            if (!string.IsNullOrWhiteSpace(c) && File.Exists(c)) return c;

        // PATH (processus + utilisateur + machine : utile si FFmpeg vient d'être installé)
        string path = string.Join(';',
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string p = Path.Combine(dir.Trim().Trim('"'), "ffmpeg.exe");
                if (File.Exists(p)) return p;
            }
            catch { }
        }

        try
        {
            string packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(packages))
                return Directory.EnumerateFiles(packages, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
        }
        catch { }

        return null;
    }

    public static void InstallWithWinget() =>
        Process.Start(new ProcessStartInfo("winget",
            "install --id Gyan.FFmpeg -e --accept-source-agreements --accept-package-agreements")
        {
            UseShellExecute = true
        });
}
