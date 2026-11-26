using System.Numerics;
using System.Text;
using System.Text.Json;

namespace LiaDesktop.Rendering;

/// <summary>LIAH binary — PBR head with named morph deltas + UVs.</summary>
public sealed class PbrMesh
{
    public float[] BasePositions { get; }
    public float[] Normals { get; }
    public float[] UVs { get; }
    public int[] Indices { get; }
    public int VertexCount { get; }
    public Dictionary<string, float[]> Shapes { get; }
    public Vector3 EyeL { get; }
    public Vector3 EyeR { get; }

    public PbrMesh(
        float[] pos, float[] nrm, float[] uvs, int[] idx,
        Dictionary<string, float[]> shapes, Vector3 eyeL, Vector3 eyeR)
    {
        BasePositions = pos;
        Normals = nrm;
        UVs = uvs;
        Indices = idx;
        VertexCount = pos.Length / 3;
        Shapes = shapes;
        EyeL = eyeL;
        EyeR = eyeR;
    }

    public float[]? Shape(string name) =>
        Shapes.TryGetValue(name, out var d) ? d : null;

    public void Morph(float jaw, float funnel, float wide, float smile, float brow, float blink, float[] dest)
    {
        var b = BasePositions;
        Array.Copy(b, dest, b.Length);
        // Mild jaw — avoid stacking viseme_aa (that spiked the tongue); keep chin skin clean
        Add(dest, Shape("jawOpen"), Math.Clamp(jaw, 0, 0.75f));
        Add(dest, Shape("mouthFunnel"), funnel);
        Add(dest, Shape("mouthPucker"), Math.Clamp(wide * 0.30f, 0, 1));
        Add(dest, Shape("mouthSmileLeft"), smile * 0.85f);
        Add(dest, Shape("mouthSmileRight"), smile * 0.85f);
        Add(dest, Shape("browInnerUp"), brow);
        Add(dest, Shape("eyeBlinkLeft"), blink);
        Add(dest, Shape("eyeBlinkRight"), blink);
    }

    static void Add(float[] dest, float[]? delta, float w)
    {
        if (delta == null || w <= 1e-5f) return;
        w = Math.Clamp(w, 0, 1.1f);
        for (var i = 0; i < dest.Length; i++)
            dest[i] += delta[i] * w;
    }

    public static PbrMesh Load(string path, string? metaPath = null)
    {
        var buf = File.ReadAllBytes(path);
        if (buf.Length < 20 || Encoding.ASCII.GetString(buf, 0, 4) != "LIAH")
            throw new InvalidDataException("not a LIAH mesh: " + path);
        var o = 4;
        var ver = BitConverter.ToInt32(buf, o); o += 4;
        if (ver != 1) throw new InvalidDataException("unsupported LIAH ver " + ver);
        var nV = BitConverter.ToInt32(buf, o); o += 4;
        var nI = BitConverter.ToInt32(buf, o); o += 4;
        var nS = BitConverter.ToInt32(buf, o); o += 4;
        var shapes = new Dictionary<string, float[]>(StringComparer.Ordinal);
        for (var s = 0; s < nS; s++)
        {
            var ln = buf[o++];
            var name = Encoding.UTF8.GetString(buf, o, ln); o += ln;
            var deltas = new float[nV * 3];
            Buffer.BlockCopy(buf, o, deltas, 0, deltas.Length * 4);
            o += deltas.Length * 4;
            shapes[name] = deltas;
        }
        var pos = new float[nV * 3];
        Buffer.BlockCopy(buf, o, pos, 0, pos.Length * 4); o += pos.Length * 4;
        var nrm = new float[nV * 3];
        Buffer.BlockCopy(buf, o, nrm, 0, nrm.Length * 4); o += nrm.Length * 4;
        var uvs = new float[nV * 2];
        Buffer.BlockCopy(buf, o, uvs, 0, uvs.Length * 4); o += uvs.Length * 4;
        var idx = new int[nI];
        Buffer.BlockCopy(buf, o, idx, 0, idx.Length * 4);

        var eyeL = new Vector3(-0.07f, 0.35f, 0.06f);
        var eyeR = new Vector3(0.07f, 0.35f, 0.06f);
        metaPath ??= Path.Combine(Path.GetDirectoryName(path) ?? "", "lia_pbr_meta.json");
        if (File.Exists(metaPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
            var root = doc.RootElement;
            eyeL = ReadVec(root, "eyeL", eyeL);
            eyeR = ReadVec(root, "eyeR", eyeR);
        }
        // Geometric neck trim: drop triangles entirely below the cut (stump + shoulder notch).
        // Shader still clips + fades for a smooth edge; this removes the jagged silhouette verts.
        idx = TrimTrianglesBelow(pos, idx, cutY: 0.305f);
        return new PbrMesh(pos, nrm, uvs, idx, shapes, eyeL, eyeR);
    }

    /// <summary>Keep a triangle only if ALL verts are at/above cutY — clean horizontal-ish trim, no stump/notch flaps.</summary>
    static int[] TrimTrianglesBelow(float[] pos, int[] idx, float cutY)
    {
        var kept = new List<int>(idx.Length);
        for (var t = 0; t + 2 < idx.Length; t += 3)
        {
            var a = idx[t]; var b = idx[t + 1]; var c = idx[t + 2];
            var ya = pos[a * 3 + 1];
            var yb = pos[b * 3 + 1];
            var yc = pos[c * 3 + 1];
            if (ya >= cutY && yb >= cutY && yc >= cutY)
            {
                kept.Add(a); kept.Add(b); kept.Add(c);
            }
        }
        return kept.ToArray();
    }

    public static PbrMesh LoadPart(string path)
    {
        var m = Load(path);
        return m;
    }

    static Vector3 ReadVec(JsonElement root, string key, Vector3 fb)
    {
        if (!root.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Array) return fb;
        return new Vector3(el[0].GetSingle(), el[1].GetSingle(), el[2].GetSingle());
    }
}
