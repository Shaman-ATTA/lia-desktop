using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace LiaDesktop.Rendering;

[StructLayout(LayoutKind.Sequential)]
struct FrameCB
{
    public Matrix4x4 ViewProj;
    public Matrix4x4 Model;
    public Vector3 CamPos;
    public float Time;
    public float Glow;
    public float Blink;
    public Vector2 Pad;
}

[StructLayout(LayoutKind.Sequential)]
struct RingCB
{
    public Vector3 Color;
    public float Opacity;
}

[StructLayout(LayoutKind.Sequential)]
struct Vertex
{
    public Vector3 Pos;
    public Vector3 Nrm;
    public Vector4 Col;
}

public sealed class HoloRenderer : IDisposable
{
    static readonly Vector3 Amber = new(1f, 0.604f, 0.235f); // #FF9A3C

    ID3D11Device? _device;
    ID3D11DeviceContext? _ctx;
    IDXGISwapChain1? _swap;
    ID3D11RenderTargetView? _rtv;
    ID3D11DepthStencilView? _dsv;
    ID3D11Buffer? _frameCb;
    ID3D11Buffer? _ringCb;
    ID3D11Buffer? _headVb;
    ID3D11Buffer? _headIb;
    ID3D11VertexShader? _vs;
    ID3D11PixelShader? _psHead;
    ID3D11PixelShader? _psRing;
    ID3D11InputLayout? _layout;
    ID3D11DepthStencilState? _dssWrite;
    ID3D11DepthStencilState? _dssRead;
    ID3D11BlendState? _blendOpaque;
    ID3D11BlendState? _blendAlpha;
    ID3D11RasterizerState? _rs;

    HoloMesh? _mesh;
    float[]? _morphScratch;
    readonly List<RingMesh> _rings = [];
    int _width, _height;
    float _time;
    float _jaw, _funnel, _wide;
    float _ptrX, _ptrY;
    float _poseYaw;
    float _nextBlink = 2.5f;
    float _blinkT;
    bool _ready;
    float _forcedYaw = float.NaN;
    float _forcedJaw = float.NaN;
    bool _freezeBlink;

    public float Jaw { set => _jaw = value; }
    public float Funnel { set => _funnel = value; }
    public float Wide { set => _wide = value; }
    public float PoseYaw { set => _poseYaw = value; }
    public void SetPointer(float x, float y) { _ptrX = x; _ptrY = y; }

    /// <summary>CI / demo overrides (NaN = idle animation). Freezes blink open for screenshots.</summary>
    public void ForcePose(float? yawDeg, float? jaw)
    {
        _forcedYaw = yawDeg.HasValue ? yawDeg.Value * MathF.PI / 180f : float.NaN;
        _forcedJaw = jaw ?? float.NaN;
        _freezeBlink = yawDeg.HasValue || jaw.HasValue;
    }

    public ID3D11Device? Device => _device;

    public void InitDevice()
    {
        try
        {
            _device = D3D11.D3D11CreateDevice(
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                new[] { FeatureLevel.Level_11_0 });
        }
        catch
        {
            _device = D3D11.D3D11CreateDevice(
                DriverType.Warp,
                DeviceCreationFlags.BgraSupport,
                new[] { FeatureLevel.Level_11_0 });
        }
        _ctx = _device.ImmediateContext;

        _vs = CompileVS(HoloShaders.Vertex, out var vsBlob);
        _psHead = CompilePS(HoloShaders.PixelHead);
        _psRing = CompilePS(HoloShaders.PixelRing);
        _layout = _device.CreateInputLayout(new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new InputElementDescription("COLOR", 0, Format.R32G32B32A32_Float, 24, 0),
        }, vsBlob.Span);

        _frameCb = _device.CreateBuffer(new BufferDescription(
            (uint)Marshal.SizeOf<FrameCB>(), BindFlags.ConstantBuffer,
            ResourceUsage.Dynamic, CpuAccessFlags.Write, ResourceOptionFlags.None, 0));
        _ringCb = _device.CreateBuffer(new BufferDescription(
            16, BindFlags.ConstantBuffer,
            ResourceUsage.Dynamic, CpuAccessFlags.Write, ResourceOptionFlags.None, 0));

        _dssWrite = _device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = DepthWriteMask.All,
            DepthFunc = ComparisonFunction.LessEqual,
        });
        _dssRead = _device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.LessEqual,
        });
        _blendOpaque = _device.CreateBlendState(BlendDescription.Opaque);
        _blendAlpha = _device.CreateBlendState(BlendDescription.AlphaBlend);
        // Mesh is authored CCW (glTF/Three) — treat CCW as front
        _rs = _device.CreateRasterizerState(new RasterizerDescription
        {
            CullMode = CullMode.Back,
            FillMode = FillMode.Solid,
            FrontCounterClockwise = true,
            DepthClipEnable = true,
        });

        BuildRings();
        _ready = true;
    }

    public void LoadMesh(string path)
    {
        _mesh = HoloMesh.Load(path);
        _morphScratch = new float[_mesh.BasePositions.Length];
        Array.Copy(_mesh.BasePositions, _morphScratch, _morphScratch.Length);
        UploadHead(_morphScratch);
    }

    public void Resize(int w, int h, IDXGISwapChain1 swap)
    {
        if (_device == null) return;
        _width = Math.Max(1, w);
        _height = Math.Max(1, h);
        _rtv?.Dispose();
        _dsv?.Dispose();
        _swap = swap;
        using var bb = swap.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _device.CreateRenderTargetView(bb);
        var depthTex = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.D24_UNorm_S8_UInt,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil,
        });
        _dsv = _device.CreateDepthStencilView(depthTex);
        depthTex.Dispose();
    }

    public IDXGIFactory2 GetDxgiFactory()
    {
        using var dxgi = _device!.QueryInterface<IDXGIDevice>();
        using var adapter = dxgi.GetAdapter();
        return adapter.GetParent<IDXGIFactory2>();
    }

    public void Tick(float dt)
    {
        if (!_ready || _device == null || _ctx == null || _rtv == null || _dsv == null || _swap == null) return;
        _time += dt;

        if (!_freezeBlink)
        {
            if (_time > _nextBlink) { _blinkT = 0.14f; _nextBlink = _time + 2.8f + Random.Shared.NextSingle() * 3.5f; }
            _blinkT = Math.Max(0, _blinkT - dt);
        }
        else
        {
            _blinkT = 0;
        }
        var blinkAmt = _freezeBlink ? 0f
            : (_blinkT > 0.07f ? (0.14f - _blinkT) / 0.07f : _blinkT / 0.07f);

        var jaw = float.IsNaN(_forcedJaw) ? _jaw : _forcedJaw;
        var funnel = float.IsNaN(_forcedJaw) ? _funnel : Math.Min(0.35f, _forcedJaw * 0.4f);
        var wide = float.IsNaN(_forcedJaw) ? _wide : 0.08f;

        if (_mesh != null && _morphScratch != null)
        {
            _mesh.Morph(Math.Min(0.72f, jaw * 0.92f), funnel * 0.55f, wide * 0.55f, _morphScratch);
            UploadHead(_morphScratch);
        }

        _ctx.OMSetRenderTargets(_rtv, _dsv);
        _ctx.RSSetViewport(new Viewport(0, 0, _width, _height));
        _ctx.ClearRenderTargetView(_rtv, new Color4(0.02f, 0.015f, 0.04f, 1));
        _ctx.ClearDepthStencilView(_dsv, DepthStencilClearFlags.Depth, 1, 0);
        _ctx.IASetInputLayout(_layout);
        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _ctx.VSSetShader(_vs);
        _ctx.RSSetState(_rs);

        var aspect = _width / (float)_height;
        // Pull back so head+neck+rings fit with top/bottom margin
        var orbit = MathF.Sin(_time * 0.09f) * 0.08f;
        var cam = new Vector3(_ptrX * 0.12f + orbit, 0.04f + _ptrY * 0.04f, 3.55f);
        var lookAt = new Vector3(0, -0.08f, 0);
        var view = Matrix4x4.CreateLookAt(cam, lookAt, Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(26f * MathF.PI / 180f, aspect, 0.05f, 40f);
        var viewProj = Matrix4x4.Transpose(view * proj);

        var yaw = float.IsNaN(_forcedYaw)
            ? _poseYaw + MathF.Sin(_time * 0.28f) * 0.22f   // gentle idle 3/4 swing
            : _forcedYaw;
        var pitch = MathF.Sin(_time * 0.23f) * 0.05f;
        var breath = 1f + MathF.Sin(_time * 1.1f) * 0.01f;
        // Head scale leaves clear air for orbiting rings
        var headModel = Matrix4x4.CreateScale(0.48f * breath)
                        * Matrix4x4.CreateRotationX(pitch)
                        * Matrix4x4.CreateRotationY(yaw)
                        * Matrix4x4.CreateTranslation(0, -0.02f, 0);
        headModel = Matrix4x4.Transpose(headModel);

        WriteFrame(viewProj, headModel, cam, blinkAmt, 1.05f);

        // 1) Head opaque + depth write
        _ctx.OMSetDepthStencilState(_dssWrite);
        _ctx.OMSetBlendState(_blendOpaque);
        _ctx.PSSetShader(_psHead);
        if (_headVb != null && _headIb != null && _mesh != null)
        {
            var stride = (uint)Marshal.SizeOf<Vertex>();
            _ctx.IASetVertexBuffer(0, _headVb, stride, 0);
            _ctx.IASetIndexBuffer(_headIb, Format.R32_UInt, 0);
            _ctx.DrawIndexed((uint)_mesh.Indices.Length, 0, 0);
        }

        // 2) Amber rings — depth-tested (behind head occluded), no depth write
        _ctx.OMSetDepthStencilState(_dssRead);
        _ctx.OMSetBlendState(_blendAlpha);
        _ctx.PSSetShader(_psRing);
        var ringStride = (uint)Marshal.SizeOf<Vertex>();
        for (var i = 0; i < _rings.Count; i++)
        {
            var r = _rings[i];
            // Orbit around head center; strong tilt so arcs clear eye/mouth band
            var rot = Matrix4x4.CreateFromYawPitchRoll(
                r.BaseYaw + _time * r.SpinY,
                r.BasePitch,
                r.BaseRoll + _time * r.SpinZ);
            var model = Matrix4x4.CreateScale(1)
                        * rot
                        * Matrix4x4.CreateTranslation(0, -0.05f, -0.08f);
            model = Matrix4x4.Transpose(model);
            WriteFrame(viewProj, model, cam, 0, 1.0f);
            WriteRing(r.Color, r.Opacity);
            _ctx.IASetVertexBuffer(0, r.Vb, ringStride, 0);
            _ctx.IASetIndexBuffer(r.Ib, Format.R32_UInt, 0);
            _ctx.DrawIndexed(r.IndexCount, 0, 0);
        }

        _swap.Present(1, PresentFlags.None);
    }

    void WriteFrame(Matrix4x4 vp, Matrix4x4 model, Vector3 cam, float blink, float glow)
    {
        var mapped = _ctx!.Map(_frameCb!, MapMode.WriteDiscard, MapFlags.None);
        var cb = new FrameCB
        {
            ViewProj = vp,
            Model = model,
            CamPos = cam,
            Time = _time,
            Glow = glow,
            Blink = blink,
        };
        unsafe { *(FrameCB*)mapped.DataPointer = cb; }
        _ctx.Unmap(_frameCb);
        _ctx.VSSetConstantBuffer(0, _frameCb);
        _ctx.PSSetConstantBuffer(0, _frameCb);
    }

    void WriteRing(Vector3 color, float opacity)
    {
        var mapped = _ctx!.Map(_ringCb!, MapMode.WriteDiscard, MapFlags.None);
        unsafe { *(RingCB*)mapped.DataPointer = new RingCB { Color = color, Opacity = opacity }; }
        _ctx.Unmap(_ringCb);
        _ctx.PSSetConstantBuffer(1, _ringCb);
    }

    void UploadHead(float[] positions)
    {
        if (_mesh == null || _device == null || _ctx == null) return;
        var n = _mesh.VertexCount;
        var stride = Marshal.SizeOf<Vertex>();
        if (_headVb == null)
        {
            _headVb = _device.CreateBuffer(new BufferDescription(
                (uint)(n * stride), BindFlags.VertexBuffer,
                ResourceUsage.Dynamic, CpuAccessFlags.Write, ResourceOptionFlags.None, 0));
            _headIb = _device.CreateBuffer(_mesh.Indices, BindFlags.IndexBuffer);
        }
        var mapped = _ctx.Map(_headVb, MapMode.WriteDiscard, MapFlags.None);
        unsafe
        {
            var dst = (Vertex*)mapped.DataPointer;
            for (var i = 0; i < n; i++)
            {
                dst[i] = new Vertex
                {
                    Pos = new Vector3(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2]),
                    Nrm = new Vector3(_mesh.Normals[i * 3], _mesh.Normals[i * 3 + 1], _mesh.Normals[i * 3 + 2]),
                    Col = new Vector4(_mesh.Colors[i * 4], _mesh.Colors[i * 4 + 1], _mesh.Colors[i * 4 + 2], _mesh.Colors[i * 4 + 3]),
                };
            }
        }
        _ctx.Unmap(_headVb);
    }

    void BuildRings()
    {
        // 3 thin amber tori, radius > head. High inclination (~70–80°) so the
        // camera-facing arcs sit above the brow / below the chin — never across eyes/mouth.
        void Add(float radius, float tube, float pitch, float yaw, float roll, float spinZ, float spinY = 0.02f, float opacity = 0.85f)
        {
            var (vb, ib, count) = CreateTorus(radius, tube, MathF.PI * 2);
            _rings.Add(new RingMesh(vb, ib, count, Amber, opacity, pitch, yaw, roll, spinZ, spinY));
        }
        Add(1.05f, 0.006f, 1.22f, 0.04f, 0.18f, 0.14f, 0.012f, 0.82f);  // ~70°
        Add(1.22f, 0.005f, -1.30f, 0.10f, -0.22f, -0.10f, 0.018f, 0.68f); // ~-74°
        Add(1.38f, 0.0045f, 1.38f, -0.06f, 0.28f, 0.07f, 0.009f, 0.52f); // ~79°
    }

    (ID3D11Buffer vb, ID3D11Buffer ib, uint count) CreateTorus(float radius, float tube, float arc)
    {
        const int seg = 128, sides = 10;
        var verts = new List<Vertex>();
        var indices = new List<int>();
        var segs = Math.Max(8, (int)(seg * (arc / (MathF.PI * 2))));
        for (var i = 0; i <= segs; i++)
        {
            var u = i / (float)segs * arc;
            for (var j = 0; j <= sides; j++)
            {
                var v = j / (float)sides * MathF.PI * 2;
                var cx = MathF.Cos(u);
                var sx = MathF.Sin(u);
                var pos = new Vector3(
                    (radius + tube * MathF.Cos(v)) * cx,
                    (radius + tube * MathF.Cos(v)) * sx,
                    tube * MathF.Sin(v));
                var nrm = Vector3.Normalize(new Vector3(MathF.Cos(v) * cx, MathF.Cos(v) * sx, MathF.Sin(v)));
                verts.Add(new Vertex { Pos = pos, Nrm = nrm, Col = new Vector4(Amber, 1) });
            }
        }
        for (var i = 0; i < segs; i++)
        for (var j = 0; j < sides; j++)
        {
            var a = i * (sides + 1) + j;
            var b = a + sides + 1;
            // CCW winding
            indices.Add(a); indices.Add(a + 1); indices.Add(b);
            indices.Add(a + 1); indices.Add(b + 1); indices.Add(b);
        }
        var vb = _device!.CreateBuffer(verts.ToArray(), BindFlags.VertexBuffer);
        var ib = _device.CreateBuffer(indices.ToArray(), BindFlags.IndexBuffer);
        return (vb, ib, (uint)indices.Count);
    }

    ID3D11VertexShader CompileVS(string src, out ReadOnlyMemory<byte> bytecode)
    {
        bytecode = Compiler.Compile(src, "main", "vs.hlsl", "vs_5_0");
        return _device!.CreateVertexShader(bytecode.Span);
    }

    ID3D11PixelShader CompilePS(string src)
    {
        var result = Compiler.Compile(src, "main", "ps.hlsl", "ps_5_0");
        return _device!.CreatePixelShader(result.Span);
    }

    public void Dispose()
    {
        foreach (var r in _rings) { r.Vb.Dispose(); r.Ib.Dispose(); }
        _rings.Clear();
        _headVb?.Dispose();
        _headIb?.Dispose();
        _frameCb?.Dispose();
        _ringCb?.Dispose();
        _rtv?.Dispose();
        _dsv?.Dispose();
        _swap?.Dispose();
        _vs?.Dispose();
        _psHead?.Dispose();
        _psRing?.Dispose();
        _layout?.Dispose();
        _dssWrite?.Dispose();
        _dssRead?.Dispose();
        _blendOpaque?.Dispose();
        _blendAlpha?.Dispose();
        _rs?.Dispose();
        _ctx?.Dispose();
        _device?.Dispose();
        _ready = false;
    }

    sealed class RingMesh
    {
        public ID3D11Buffer Vb, Ib;
        public uint IndexCount;
        public Vector3 Color;
        public float Opacity, BasePitch, BaseYaw, BaseRoll, SpinZ, SpinY;
        public RingMesh(ID3D11Buffer vb, ID3D11Buffer ib, uint count, Vector3 c, float o,
            float pitch, float yaw, float roll, float spinZ, float spinY)
        {
            Vb = vb; Ib = ib; IndexCount = count; Color = c; Opacity = o;
            BasePitch = pitch; BaseYaw = yaw; BaseRoll = roll;
            SpinZ = spinZ; SpinY = spinY;
        }
    }
}
