using System;
using System.IO;

namespace WheelConvert.Core;

/// <summary>Journal de diagnostic : %AppData%\WheelConvert\log.txt (désactivable dans les paramètres).</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(AppSettings.Folder, "log.txt");

    public static void Write(string message)
    {
        if (!AppSettings.Current.DebugLog) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.Folder);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 512 * 1024) info.Delete(); // garde le fichier petit
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff}  {message}\r\n");
            }
        }
        catch { }
    }
}
