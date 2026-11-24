using System;
using System.Collections.Generic;
using System.IO;

namespace WheelConvert.Core;

public enum FileKind { Other, Image, Video, Audio, Model3D }

public static class FileKinds
{
    private static readonly HashSet<string> Images = Set(
        ".jpg", ".jpeg", ".jfif", ".png", ".webp", ".avif", ".heic", ".heif",
        ".bmp", ".gif", ".tif", ".tiff", ".ico", ".tga", ".jxl", ".psd");

    private static readonly HashSet<string> Videos = Set(
        ".mp4", ".mov", ".mkv", ".avi", ".webm", ".wmv", ".flv", ".m4v",
        ".mpg", ".mpeg", ".3gp", ".ts", ".mts");

    private static readonly HashSet<string> Audios = Set(
        ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wma", ".aiff", ".aif");

    private static readonly HashSet<string> Models = Set(
        ".stl", ".obj", ".3mf", ".ply", ".fbx", ".glb", ".gltf", ".dae", ".3ds");

    public static FileKind Detect(string path)
    {
        if (Directory.Exists(path)) return FileKind.Other;
        string ext = Path.GetExtension(path);
        if (Images.Contains(ext)) return FileKind.Image;
        if (Videos.Contains(ext)) return FileKind.Video;
        if (Audios.Contains(ext)) return FileKind.Audio;
        if (Models.Contains(ext)) return FileKind.Model3D;
        return FileKind.Other;
    }

    private static HashSet<string> Set(params string[] items) => new(items, StringComparer.OrdinalIgnoreCase);
}
