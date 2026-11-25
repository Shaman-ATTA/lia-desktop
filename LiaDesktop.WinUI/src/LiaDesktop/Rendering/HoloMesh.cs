using System.Text;
using System.Text.Json;

namespace LiaDesktop.Rendering;

/// <summary>Port of Electron HoloMesh — lia_holo_mesh.bin with jaw/funnel/wide morphs.</summary>
public sealed class HoloMesh
{
    public float[] BasePositions { get; }
    public float[] JawDeltas { get; }
    public float[] FunnelDeltas { get; }
    public float[] WideDeltas { get; }
    public float[] Normals { get; }
    public float[] Colors { get; }
    public int[] Indices { get; }
    public int VertexCount { get; }

    HoloMesh(
        float[] pos, float[] jaw, float[] funnel, float[] wide,
        float[] nrm, float[] col, int[] idx, int n)
    {
        BasePositions = pos;
        JawDeltas = jaw;
        FunnelDeltas = funnel;
        WideDeltas = wide;
        Normals = nrm;
        Colors = col;
        Indices = idx;
        VertexCount = n;
    }

    public void Morph(float jaw, float funnel, float wide, float[] dest)
    {
        jaw = Math.Clamp(jaw, 0, 1.05f);
        funnel = Math.Clamp(funnel, 0, 1);
        wide = Math.Clamp(wide, 0, 1);
        var b = BasePositions;
        for (var i = 0; i < b.Length; i++)
            dest[i] = b[i] + JawDeltas[i] * jaw + FunnelDeltas[i] * funnel + WideDeltas[i] * wide;
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
        var indices = new int[indexCount];
        for (var i = 0; i < indexCount; i++)
        {
            indices[i] = BitConverter.ToInt32(buf, o);
            o += 4;
        }
        return new HoloMesh(pos, jaw, funnel, wide, nrm, col, indices, n);
    }
}
