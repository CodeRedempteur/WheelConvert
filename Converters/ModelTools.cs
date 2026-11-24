using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Xml.Linq;
using WheelConvert.Core;

namespace WheelConvert.Converters;

/// <summary>Maillage triangulé minimal (positions + indices).</summary>
public sealed class TriMesh
{
    public List<Vector3> V { get; } = new();
    public List<int> T { get; } = new();
    private readonly Dictionary<Vector3, int> _shared = new();

    public int TriangleCount => T.Count / 3;

    /// Ajoute un sommet en fusionnant les doublons (STL n'a pas d'indices).
    public int AddShared(Vector3 v)
    {
        if (_shared.TryGetValue(v, out int i)) return i;
        i = V.Count;
        V.Add(v);
        _shared[v] = i;
        return i;
    }

    public void Tri(int a, int b, int c)
    {
        if (a == b || b == c || a == c) return; // triangle dégénéré
        int n = V.Count;
        if ((uint)a >= (uint)n || (uint)b >= (uint)n || (uint)c >= (uint)n) return;
        T.Add(a); T.Add(b); T.Add(c);
    }
}

/// <summary>Conversions 3D : lecture STL/OBJ/3MF (interne) + FBX/GLB/GLTF/PLY/DAE/3DS (Assimp), écriture STL/3MF/OBJ/PLY.</summary>
public static class ModelTools
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string ToStl(string s) => ConvertTo(s, ".stl");
    public static string To3mf(string s) => ConvertTo(s, ".3mf");
    public static string ToObj(string s) => ConvertTo(s, ".obj");
    public static string ToPly(string s) => ConvertTo(s, ".ply");

    private static string ConvertTo(string source, string ext)
    {
        var mesh = Load(source);
        if (mesh.TriangleCount == 0) throw new InvalidDataException("Aucun triangle trouvé dans le modèle.");

        string output = OutputPaths.For(source, ext);
        try
        {
            switch (ext)
            {
                case ".stl": WriteStl(mesh, output); break;
                case ".3mf": Write3mf(mesh, output); break;
                case ".obj": WriteObj(mesh, output); break;
                case ".ply": WritePly(mesh, output); break;
            }
        }
        catch
        {
            OutputPaths.TryDelete(output);
            throw;
        }
        return output;
    }

    private static TriMesh Load(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".stl" => ReadStl(path),
        ".obj" => ReadObj(path),
        ".3mf" => Read3mf(path),
        _ => ReadAssimp(path)
    };

    // ================= Lecture =================

    private static TriMesh ReadStl(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        var m = new TriMesh();

        // Binaire : 80 octets d'en-tête + uint32 + 50 octets par triangle
        if (b.Length >= 84)
        {
            uint n = BitConverter.ToUInt32(b, 80);
            if (84L + 50L * n == b.Length)
            {
                for (long i = 0; i < n; i++)
                {
                    int o = (int)(84 + i * 50 + 12); // on saute la normale
                    m.Tri(m.AddShared(ReadVec(b, o)), m.AddShared(ReadVec(b, o + 12)), m.AddShared(ReadVec(b, o + 24)));
                }
                return m;
            }
        }

        // ASCII
        var corner = new List<int>(3);
        foreach (var raw in Encoding.ASCII.GetString(b).Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith("vertex", StringComparison.OrdinalIgnoreCase)) continue;
            var p = SplitWs(line);
            if (p.Length < 4) continue;
            corner.Add(m.AddShared(new Vector3(F(p[1]), F(p[2]), F(p[3]))));
            if (corner.Count == 3)
            {
                m.Tri(corner[0], corner[1], corner[2]);
                corner.Clear();
            }
        }
        return m;
    }

    private static TriMesh ReadObj(string path)
    {
        var m = new TriMesh();
        var face = new List<int>();
        foreach (var raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.StartsWith("v ") || line.StartsWith("v\t"))
            {
                var p = SplitWs(line);
                if (p.Length >= 4) m.V.Add(new Vector3(F(p[1]), F(p[2]), F(p[3])));
            }
            else if (line.StartsWith("f ") || line.StartsWith("f\t"))
            {
                var p = SplitWs(line);
                face.Clear();
                for (int i = 1; i < p.Length; i++)
                {
                    string tok = p[i];
                    int slash = tok.IndexOf('/');
                    if (slash >= 0) tok = tok[..slash];
                    if (!int.TryParse(tok, NumberStyles.Integer, Inv, out int idx)) continue;
                    face.Add(idx < 0 ? m.V.Count + idx : idx - 1);
                }
                for (int i = 1; i + 1 < face.Count; i++) m.Tri(face[0], face[i], face[i + 1]); // éventail
            }
        }
        return m;
    }

    private static TriMesh Read3mf(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var models = zip.Entries
            .Where(e => e.FullName.EndsWith(".model", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (models.Count == 0) throw new InvalidDataException("Fichier 3MF sans modèle 3D.");

        var m = new TriMesh();
        foreach (var entry in models)
        {
            XDocument doc;
            using (var s = entry.Open()) doc = XDocument.Load(s);

            foreach (var mesh in doc.Descendants().Where(e => e.Name.LocalName == "mesh"))
            {
                int b = m.V.Count;
                foreach (var v in mesh.Descendants().Where(e => e.Name.LocalName == "vertex"))
                    m.V.Add(new Vector3(F(Attr(v, "x")), F(Attr(v, "y")), F(Attr(v, "z"))));
                foreach (var t in mesh.Descendants().Where(e => e.Name.LocalName == "triangle"))
                    m.Tri(b + I(Attr(t, "v1")), b + I(Attr(t, "v2")), b + I(Attr(t, "v3")));
            }
        }
        return m;
    }

    private static TriMesh ReadAssimp(string path)
    {
        try
        {
            using var ctx = new Assimp.AssimpContext();
            var scene = ctx.ImportFile(path,
                Assimp.PostProcessSteps.Triangulate |
                Assimp.PostProcessSteps.PreTransformVertices |
                Assimp.PostProcessSteps.JoinIdenticalVertices);

            var m = new TriMesh();
            foreach (var mesh in scene.Meshes)
            {
                int b = m.V.Count;
                foreach (var v in mesh.Vertices) m.V.Add(new Vector3(v.X, v.Y, v.Z));
                foreach (var f in mesh.Faces)
                    if (f.IndexCount == 3) m.Tri(b + f.Indices[0], b + f.Indices[1], b + f.Indices[2]);
            }
            return m;
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or TypeInitializationException)
        {
            throw new InvalidOperationException($"Moteur Assimp indisponible pour ce format : {ex.Message}");
        }
    }

    // ================= Écriture =================

    private static void WriteStl(TriMesh m, string path)
    {
        using var w = new BinaryWriter(File.Create(path));
        var header = new byte[80];
        Encoding.ASCII.GetBytes("WheelConvert binary STL").CopyTo(header, 0);
        w.Write(header);
        w.Write((uint)m.TriangleCount);
        for (int i = 0; i < m.T.Count; i += 3)
        {
            Vector3 a = m.V[m.T[i]], b = m.V[m.T[i + 1]], c = m.V[m.T[i + 2]];
            var n = Vector3.Cross(b - a, c - a);
            float len = n.Length();
            n = len > 0 ? n / len : Vector3.Zero;
            WriteVec(w, n); WriteVec(w, a); WriteVec(w, b); WriteVec(w, c);
            w.Write((ushort)0);
        }
    }

    private static void WriteObj(TriMesh m, string path)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(false)) { NewLine = "\n" };
        w.WriteLine("# WheelConvert");
        foreach (var v in m.V) w.WriteLine($"v {S(v.X)} {S(v.Y)} {S(v.Z)}");
        for (int i = 0; i < m.T.Count; i += 3)
            w.WriteLine($"f {m.T[i] + 1} {m.T[i + 1] + 1} {m.T[i + 2] + 1}");
    }

    private static void WritePly(TriMesh m, string path)
    {
        using var w = new BinaryWriter(File.Create(path));
        string header =
            "ply\nformat binary_little_endian 1.0\ncomment WheelConvert\n" +
            $"element vertex {m.V.Count}\nproperty float x\nproperty float y\nproperty float z\n" +
            $"element face {m.TriangleCount}\nproperty list uchar int vertex_indices\nend_header\n";
        w.Write(Encoding.ASCII.GetBytes(header));
        foreach (var v in m.V) WriteVec(w, v);
        for (int i = 0; i < m.T.Count; i += 3)
        {
            w.Write((byte)3);
            w.Write(m.T[i]); w.Write(m.T[i + 1]); w.Write(m.T[i + 2]);
        }
    }

    private static void Write3mf(TriMesh m, string path)
    {
        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);

        WriteText(zip, "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">\n" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>\n" +
            "<Default Extension=\"model\" ContentType=\"application/vnd.ms-package.3dmanufacturing-3dmodel+xml\"/>\n" +
            "</Types>\n");

        WriteText(zip, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">\n" +
            "<Relationship Target=\"/3D/3dmodel.model\" Id=\"rel0\" Type=\"http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel\"/>\n" +
            "</Relationships>\n");

        var entry = zip.CreateEntry("3D/3dmodel.model", CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false)) { NewLine = "\n" };
        w.WriteLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        w.WriteLine("<model unit=\"millimeter\" xml:lang=\"en-US\" xmlns=\"http://schemas.microsoft.com/3dmanufacturing/core/2015/02\">");
        w.WriteLine("<metadata name=\"Application\">WheelConvert</metadata>");
        w.WriteLine("<resources><object id=\"1\" type=\"model\"><mesh><vertices>");
        foreach (var v in m.V)
            w.WriteLine($"<vertex x=\"{S(v.X)}\" y=\"{S(v.Y)}\" z=\"{S(v.Z)}\"/>");
        w.WriteLine("</vertices><triangles>");
        for (int i = 0; i < m.T.Count; i += 3)
            w.WriteLine($"<triangle v1=\"{m.T[i]}\" v2=\"{m.T[i + 1]}\" v3=\"{m.T[i + 2]}\"/>");
        w.WriteLine("</triangles></mesh></object></resources>");
        w.WriteLine("<build><item objectid=\"1\"/></build>");
        w.WriteLine("</model>");
    }

    // ================= Utilitaires =================

    private static void WriteText(ZipArchive zip, string name, string content)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    private static Vector3 ReadVec(byte[] b, int o) =>
        new(BitConverter.ToSingle(b, o), BitConverter.ToSingle(b, o + 4), BitConverter.ToSingle(b, o + 8));

    private static void WriteVec(BinaryWriter w, Vector3 v)
    {
        w.Write(v.X); w.Write(v.Y); w.Write(v.Z);
    }

    private static string[] SplitWs(string line) =>
        line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static float F(string s) => float.Parse(s, NumberStyles.Float, Inv);
    private static int I(string s) => int.Parse(s, NumberStyles.Integer, Inv);
    private static string S(float f) => f.ToString("G9", Inv);
    private static string Attr(XElement e, string name) => (string?)e.Attribute(name) ?? "0";
}
