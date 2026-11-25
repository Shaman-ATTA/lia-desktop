using System.Numerics;
using System.Text;
using System.Text.Json;

namespace LiaDesktop.Rendering;

/// <summary>lia_holo_mesh.bin — base + jaw/funnel/wide (+ optional smile/brow) morphs.</summary>
public sealed class HoloMesh
{
    public float[] BasePositions { get; }
    public float[] JawDeltas { get; }
    public float[] FunnelDeltas { get; }
    public float[] WideDeltas { get; }
    public float[] SmileDeltas { get; }
    public float[] BrowDeltas { get; }
    public float[] Normals { get; }
    public float[] Colors { get; }
    public int[] Indices { get; }
    public int VertexCount { get; }
    public Vector3 EyeL { get; }
    public Vector3 EyeR { get; }
    public Vector3 MouthCenter { get; }

    HoloMesh(
        float[] pos, float[] jaw, float[] funnel, float[] wide,
        float[] smile, float[] brow,
        float[] nrm, float[] col, int[] idx, int n,
        Vector3 eyeL, Vector3 eyeR, Vector3 mouth)
    {
        BasePositions = pos;
        JawDeltas = jaw;
        FunnelDeltas = funnel;
        WideDeltas = wide;
        SmileDeltas = smile;
        BrowDeltas = brow;
        Normals = nrm;
        Colors = col;
        Indices = idx;
        VertexCount = n;
        EyeL = eyeL;
        EyeR = eyeR;
        MouthCenter = mouth;
    }

    public void Morph(float jaw, float funnel, float wide, float smile, float brow, float[] dest)
    {
        jaw = Math.Clamp(jaw, 0, 1.05f);
        funnel = Math.Clamp(funnel, 0, 1);
        wide = Math.Clamp(wide, 0, 1);
        smile = Math.Clamp(smile, 0, 1);
        brow = Math.Clamp(brow, 0, 1);
        var b = BasePositions;
        for (var i = 0; i < b.Length; i++)
            dest[i] = b[i]
                + JawDeltas[i] * jaw
                + FunnelDeltas[i] * funnel
                + WideDeltas[i] * wide
                + SmileDeltas[i] * smile
                + BrowDeltas[i] * brow;
    }

    public static HoloMesh Load(string path)
    {
        var buf = File.ReadAllBytes(path);
        var o = 0;
        var metaLen = BitConverter.ToInt32(buf, o); o += 4;
        var metaJson = Encoding.UTF8.GetString(buf, o, metaLen); o += metaLen;
        using var doc = JsonDocument.Parse(metaJson);
        var root = doc.RootElement;
        var n = root.GetProperty("vertexCount").GetInt32();
        var indexCount = root.GetProperty("indexCount").GetInt32();
        var hasExtra = root.TryGetProperty("has_extra_morphs", out var he) && he.GetBoolean();
        var hasExpr = root.TryGetProperty("has_expression_morphs", out var hx) && hx.GetBoolean();

        Vector3 ReadVec(string key, Vector3 fallback)
        {
            if (!root.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Array) return fallback;
            return new Vector3(el[0].GetSingle(), el[1].GetSingle(), el[2].GetSingle());
        }

        float[] ReadFloats(int count)
        {
            var a = new float[count];
            Buffer.BlockCopy(buf, o, a, 0, count * 4);
            o += count * 4;
            return a;
        }

        var pos = ReadFloats(n * 3);
        var nrm = ReadFloats(n * 3);
        _ = ReadFloats(n * 2); // uvs
        var col = ReadFloats(n * 4);
        var jaw = ReadFloats(n * 3);
        var funnel = hasExtra ? ReadFloats(n * 3) : new float[n * 3];
        var wide = hasExtra ? ReadFloats(n * 3) : new float[n * 3];
        var smile = hasExpr ? ReadFloats(n * 3) : new float[n * 3];
        var brow = hasExpr ? ReadFloats(n * 3) : new float[n * 3];
        var indices = new int[indexCount];
        for (var i = 0; i < indexCount; i++)
        {
            indices[i] = BitConverter.ToInt32(buf, o);
            o += 4;
        }

        return new HoloMesh(
            pos, jaw, funnel, wide, smile, brow, nrm, col, indices, n,
            ReadVec("eye_L", new Vector3(-0.16f, 0.25f, 0.40f)),
            ReadVec("eye_R", new Vector3(0.16f, 0.25f, 0.40f)),
            ReadVec("mouth_center", new Vector3(0, -0.06f, 0.44f)));
    }
}

/// <summary>Static companion mesh (hair / lashes) — same header, no morphs.</summary>
public sealed class HoloPartMesh : IDisposable
{
    public float[] Positions { get; }
    public float[] Normals { get; }
    public float[] Colors { get; }
    public int[] Indices { get; }
    public int VertexCount { get; }

    HoloPartMesh(float[] pos, float[] nrm, float[] col, int[] idx, int n)
    {
        Positions = pos;
        Normals = nrm;
        Colors = col;
        Indices = idx;
        VertexCount = n;
    }

    public static HoloPartMesh? TryLoad(string path)
    {
        if (!File.Exists(path)) return null;
        var buf = File.ReadAllBytes(path);
        var o = 0;
        var metaLen = BitConverter.ToInt32(buf, o); o += 4;
        var metaJson = Encoding.UTF8.GetString(buf, o, metaLen); o += metaLen;
        using var doc = JsonDocument.Parse(metaJson);
        var root = doc.RootElement;
        var n = root.GetProperty("vertexCount").GetInt32();
        var indexCount = root.GetProperty("indexCount").GetInt32();

        float[] ReadFloats(int count)
        {
            var a = new float[count];
            Buffer.BlockCopy(buf, o, a, 0, count * 4);
            o += count * 4;
            return a;
        }

        var pos = ReadFloats(n * 3);
        var nrm = ReadFloats(n * 3);
        _ = ReadFloats(n * 2);
        var col = ReadFloats(n * 4);
        var indices = new int[indexCount];
        for (var i = 0; i < indexCount; i++)
        {
            indices[i] = BitConverter.ToInt32(buf, o);
            o += 4;
        }
        return new HoloPartMesh(pos, nrm, col, indices, n);
    }

    public void Dispose() { }
}
