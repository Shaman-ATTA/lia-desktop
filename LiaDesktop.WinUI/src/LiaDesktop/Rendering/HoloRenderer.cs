using System.Drawing;
using System.Drawing.Imaging;
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
    public Vector2 Uv;
}

public sealed class HoloRenderer : IDisposable
{
    static readonly Vector3 Amber = new(1f, 0.604f, 0.235f);
    const float EyeRadius = 0.022f;
    // Face Y≈0.20..0.55 local; eyes ≈0.39 — place eyes near world Y=0.05
    const float HeadScale = 2.85f;
    const float HeadTransY = -1.06f;
    const int MsaaCount = 4;

    ID3D11Device? _device;
    ID3D11DeviceContext? _ctx;
    IDXGISwapChain1? _swap;
    ID3D11Texture2D? _msaaColor;
    ID3D11Texture2D? _msaaDepth;
    ID3D11RenderTargetView? _rtvMsaa;
    ID3D11DepthStencilView? _dsvMsaa;
    ID3D11RenderTargetView? _rtvResolve;
    ID3D11Buffer? _frameCb;
    ID3D11Buffer? _ringCb;
    ID3D11Buffer? _headVb;
    ID3D11Buffer? _headIb;
    ID3D11Buffer? _hairVb;
    ID3D11Buffer? _hairIb;
    uint _hairIndexCount;
    ID3D11VertexShader? _vs;
    ID3D11PixelShader? _psSkin;
    ID3D11PixelShader? _psHair;
    ID3D11PixelShader? _psEye;
    ID3D11PixelShader? _psIris;
    ID3D11PixelShader? _psCornea;
    ID3D11PixelShader? _psLid;
    ID3D11PixelShader? _psRing;
    ID3D11InputLayout? _layout;
    ID3D11DepthStencilState? _dssWrite;
    ID3D11DepthStencilState? _dssRead;
    ID3D11BlendState? _blendOpaque;
    ID3D11BlendState? _blendAlpha;
    ID3D11RasterizerState? _rs;
    ID3D11RasterizerState? _rsNoCull;
    ID3D11SamplerState? _samplerLinearWrap;
    ID3D11ShaderResourceView? _srvAlbedo;
    ID3D11ShaderResourceView? _srvNormal;
    ID3D11ShaderResourceView? _srvOrm;
    ID3D11ShaderResourceView? _srvOpacity;

    PbrMesh? _mesh;
    float[]? _morphScratch;
    readonly List<RingMesh> _rings = [];
    PartMesh? _eyeSphere;
    PartMesh? _irisDisc;
    PartMesh? _corneaShell;
    PartMesh? _lidUpper;
    PartMesh? _lidLower;

    int _width, _height;
    float _time;
    float _jaw, _funnel, _wide;
    float _ptrX, _ptrY;
    float _poseYaw;
    float _nextBlink = 3.2f;
    float _blinkPhase;
    bool _ready;
    float _forcedYaw = float.NaN;
    float _forcedJaw = float.NaN;
    bool _freezeBlink;
    bool _eyesCloseup;
    bool _ringsShowcase;
    float _gazeYaw, _gazePitch;
    float _saccadeYaw, _saccadePitch;
    float _nextSaccade = 1.2f;
    float _headYaw, _headPitch;
    float _idleYawTarget, _idlePitchTarget;
    float _nextIdleWander = 2.5f;
    bool _listening;
    Vector3 _eyeL = new(-0.06f, 0.39f, 0.05f);
    Vector3 _eyeR = new(0.06f, 0.39f, 0.05f);

    public float Jaw { set => _jaw = value; }
    public float Funnel { set => _funnel = value; }
    public float Wide { set => _wide = value; }
    public float PoseYaw { set => _poseYaw = value; }
    public void SetPointer(float x, float y) { _ptrX = x; _ptrY = y; }
    public void SetListening(bool listening) => _listening = listening;

    public void ForcePose(float? yawDeg, float? jaw, bool eyesCloseup = false, bool ringsShowcase = false)
    {
        _forcedYaw = yawDeg.HasValue ? yawDeg.Value * MathF.PI / 180f : float.NaN;
        _forcedJaw = jaw ?? float.NaN;
        _freezeBlink = yawDeg.HasValue || jaw.HasValue || eyesCloseup || ringsShowcase;
        _eyesCloseup = eyesCloseup;
        _ringsShowcase = ringsShowcase;
        if (ringsShowcase) _time = 1.85f;
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
        _psSkin = CompilePS(HoloShaders.PixelSkin);
        _psHair = CompilePS(HoloShaders.PixelHair);
        _psEye = CompilePS(HoloShaders.PixelEye);
        _psIris = CompilePS(HoloShaders.PixelIris);
        _psCornea = CompilePS(HoloShaders.PixelCornea);
        _psLid = CompilePS(HoloShaders.PixelLid);
        _psRing = CompilePS(HoloShaders.PixelRing);
        _layout = _device.CreateInputLayout(new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 24, 0),
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
        _rs = _device.CreateRasterizerState(new RasterizerDescription
        {
            CullMode = CullMode.Back,
            FillMode = FillMode.Solid,
            FrontCounterClockwise = true,
            DepthClipEnable = true,
            MultisampleEnable = true,
        });
        _rsNoCull = _device.CreateRasterizerState(new RasterizerDescription
        {
            CullMode = CullMode.None,
            FillMode = FillMode.Solid,
            FrontCounterClockwise = true,
            DepthClipEnable = true,
            MultisampleEnable = true,
        });
        _samplerLinearWrap = _device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            MaxAnisotropy = 1,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD = 0,
            MaxLOD = float.MaxValue,
        });

        BuildRings();
        BuildEyes();
        _ready = true;
    }

    public void LoadMesh(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        var meshPath = path;
        if (!File.Exists(meshPath) ||
            meshPath.Contains("lia_holo_mesh.bin", StringComparison.OrdinalIgnoreCase))
        {
            var alt = Path.Combine(dir, "lia_pbr_head.bin");
            if (File.Exists(alt)) meshPath = alt;
        }

        _mesh = PbrMesh.Load(meshPath);
        _eyeL = _mesh.EyeL;
        _eyeR = _mesh.EyeR;
        _morphScratch = new float[_mesh.BasePositions.Length];
        Array.Copy(_mesh.BasePositions, _morphScratch, _morphScratch.Length);
        UploadHead(_morphScratch);

        LoadPbrPart(Path.Combine(dir, "lia_pbr_hair.bin"), ref _hairVb, ref _hairIb, ref _hairIndexCount);

        _srvAlbedo = TryLoadTexturePng(Path.Combine(dir, "lia_pbr_albedo.png"));
        _srvNormal = TryLoadTexturePng(Path.Combine(dir, "lia_pbr_normal.png"));
        _srvOrm = TryLoadTexturePng(Path.Combine(dir, "lia_pbr_orm.png"));
        _srvOpacity = TryLoadTexturePng(Path.Combine(dir, "lia_pbr_opacity.png"));
    }

    void LoadPbrPart(string path, ref ID3D11Buffer? vb, ref ID3D11Buffer? ib, ref uint indexCount)
    {
        if (!File.Exists(path) || _device == null) return;
        var part = PbrMesh.LoadPart(path);
        vb?.Dispose();
        ib?.Dispose();
        var n = part.VertexCount;
        var verts = new Vertex[n];
        for (var i = 0; i < n; i++)
        {
            verts[i] = new Vertex
            {
                Pos = new Vector3(part.BasePositions[i * 3], part.BasePositions[i * 3 + 1], part.BasePositions[i * 3 + 2]),
                Nrm = new Vector3(part.Normals[i * 3], part.Normals[i * 3 + 1], part.Normals[i * 3 + 2]),
                Uv = new Vector2(part.UVs[i * 2], part.UVs[i * 2 + 1]),
            };
        }
        vb = _device.CreateBuffer(verts, BindFlags.VertexBuffer);
        ib = _device.CreateBuffer(part.Indices, BindFlags.IndexBuffer);
        indexCount = (uint)part.Indices.Length;
    }

    ID3D11ShaderResourceView? TryLoadTexturePng(string path)
    {
        if (!File.Exists(path) || _device == null) return null;
        using var bmp = new Bitmap(path);
        return CreateTextureFromBitmap(bmp);
    }

    unsafe ID3D11ShaderResourceView CreateTextureFromBitmap(Bitmap bmp)
    {
        var w = bmp.Width;
        var h = bmp.Height;
        var rect = new Rectangle(0, 0, w, h);
        var locked = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[w * h * 4];
            var src = (byte*)locked.Scan0;
            var stride = locked.Stride;
            for (var y = 0; y < h; y++)
            {
                var row = src + y * stride;
                for (var x = 0; x < w; x++)
                {
                    var i = (y * w + x) * 4;
                    // BGRA → RGBA
                    bytes[i] = row[x * 4 + 2];
                    bytes[i + 1] = row[x * 4 + 1];
                    bytes[i + 2] = row[x * 4];
                    bytes[i + 3] = row[x * 4 + 3];
                }
            }

            var desc = new Texture2DDescription
            {
                Width = (uint)w,
                Height = (uint)h,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
            };
            var tex = _device!.CreateTexture2D(desc);
            fixed (byte* p = bytes)
            {
                _ctx!.UpdateSubresource(tex, 0, null, (IntPtr)p, (uint)(w * 4), (uint)(bytes.Length));
            }
            return _device.CreateShaderResourceView(tex);
        }
        finally
        {
            bmp.UnlockBits(locked);
        }
    }

    public void Resize(int w, int h, IDXGISwapChain1 swap)
    {
        if (_device == null) return;
        _width = Math.Max(1, w);
        _height = Math.Max(1, h);
        _rtvMsaa?.Dispose();
        _dsvMsaa?.Dispose();
        _msaaColor?.Dispose();
        _msaaDepth?.Dispose();
        _rtvResolve?.Dispose();
        _swap = swap;

        using var bb = swap.GetBuffer<ID3D11Texture2D>(0);
        _rtvResolve = _device.CreateRenderTargetView(bb);

        var sample = new SampleDescription(MsaaCount, 0);
        try
        {
            _msaaColor = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_width,
                Height = (uint)_height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = sample,
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
            });
        }
        catch
        {
            sample = new SampleDescription(1, 0);
            _msaaColor = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_width,
                Height = (uint)_height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = sample,
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
            });
        }
        _rtvMsaa = _device.CreateRenderTargetView(_msaaColor);
        _msaaDepth = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.D24_UNorm_S8_UInt,
            SampleDescription = sample,
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil,
        });
        _dsvMsaa = _device.CreateDepthStencilView(_msaaDepth);
    }

    public IDXGIFactory2 GetDxgiFactory()
    {
        using var dxgi = _device!.QueryInterface<IDXGIDevice>();
        using var adapter = dxgi.GetAdapter();
        return adapter.GetParent<IDXGIFactory2>();
    }

    public void Tick(float dt)
    {
        if (!_ready || _device == null || _ctx == null || _rtvMsaa == null || _dsvMsaa == null || _swap == null || _rtvResolve == null)
            return;
        _time += dt;

        UpdateBlink(dt);
        UpdateHeadLook(dt);
        UpdateGaze(dt);
        var blinkAmt = _blinkPhase;

        var jaw = float.IsNaN(_forcedJaw) ? _jaw : _forcedJaw;
        var funnel = float.IsNaN(_forcedJaw) ? _funnel : Math.Min(0.35f, _forcedJaw * 0.4f);
        var wide = float.IsNaN(_forcedJaw) ? _wide : 0.08f;

        var smile = float.IsNaN(_forcedJaw)
            ? 0.12f + 0.06f * MathF.Sin(_time * 0.35f)
            : 0.08f;
        var brow = float.IsNaN(_forcedJaw)
            ? 0.08f + 0.05f * MathF.Sin(_time * 0.41f + 1.2f)
            : 0.05f;

        if (_mesh != null && _morphScratch != null)
        {
            var lid = BlinkLidAmount();
            _mesh.Morph(Math.Min(0.72f, jaw * 0.92f), funnel * 0.55f, wide * 0.55f, smile, brow, lid, _morphScratch);
            UploadHead(_morphScratch);
        }

        _ctx.OMSetRenderTargets(_rtvMsaa, _dsvMsaa);
        _ctx.RSSetViewport(new Viewport(0, 0, _width, _height));
        _ctx.ClearRenderTargetView(_rtvMsaa, new Color4(0.015f, 0.012f, 0.03f, 1));
        _ctx.ClearDepthStencilView(_dsvMsaa, DepthStencilClearFlags.Depth, 1, 0);
        _ctx.IASetInputLayout(_layout);
        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _ctx.VSSetShader(_vs);
        _ctx.RSSetState(_rs);
        if (_samplerLinearWrap != null)
            _ctx.PSSetSamplers(0, new[] { _samplerLinearWrap });

        var aspect = _width / (float)_height;
        Vector3 cam, lookAt;
        float fovDeg;
        float nearPlane = 0.08f;
        if (_eyesCloseup)
        {
            lookAt = new Vector3(0f, 0.09f, 0.12f);
            cam = new Vector3(0f, 0.10f, 0.95f);
            fovDeg = 18f;
            nearPlane = 0.12f;
        }
        else if (_ringsShowcase)
        {
            var orbit = MathF.Sin(_time * 0.14f) * 0.08f;
            cam = new Vector3(orbit, 0.08f, 2.15f);
            lookAt = new Vector3(0, 0.05f, 0.05f);
            fovDeg = 28f;
        }
        else
        {
            var orbit = MathF.Sin(_time * 0.09f) * 0.05f;
            cam = new Vector3(orbit, 0.08f, 2.25f);
            lookAt = new Vector3(0, 0.05f, 0.05f);
            fovDeg = 24f;
        }
        var view = Matrix4x4.CreateLookAt(cam, lookAt, Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(fovDeg * MathF.PI / 180f, aspect, nearPlane, 40f);
        var viewProj = Matrix4x4.Transpose(view * proj);

        var yaw = float.IsNaN(_forcedYaw) ? _headYaw : _forcedYaw;
        var pitch = _eyesCloseup ? 0f : _headPitch;
        var breath = 1f + MathF.Sin(_time * 1.05f) * 0.008f;
        var breathY = MathF.Sin(_time * 1.05f) * 0.006f;
        var headWorld = Matrix4x4.CreateScale(HeadScale * breath)
                        * Matrix4x4.CreateRotationX(pitch)
                        * Matrix4x4.CreateRotationY(yaw)
                        * Matrix4x4.CreateTranslation(0, HeadTransY + breathY, 0);
        var headModel = Matrix4x4.Transpose(headWorld);
        var stride = (uint)Marshal.SizeOf<Vertex>();

        // 1) Skin — PBR textures, light alpha on edges
        _ctx.OMSetDepthStencilState(_dssWrite);
        _ctx.OMSetBlendState(_blendAlpha, new Color4(0, 0, 0, 1), 0xFFFFFFFF);
        WriteFrame(viewProj, headModel, cam, blinkAmt, 1.0f);
        BindSkinTextures();
        _ctx.PSSetShader(_psSkin);
        if (_headVb != null && _headIb != null && _mesh != null)
        {
            _ctx.IASetVertexBuffer(0, _headVb, stride, 0);
            _ctx.IASetIndexBuffer(_headIb, Format.R32_UInt, 0);
            _ctx.DrawIndexed((uint)_mesh.Indices.Length, 0, 0);
        }
        _ctx.OMSetBlendState(_blendOpaque, new Color4(0, 0, 0, 1), 0xFFFFFFFF);

        // 2) Eyes + cornea + lids
        DrawEyes(viewProj, headWorld, cam, blinkAmt);

        // 3) Hair — alpha, depth-tested, no depth write, no cull
        if (_hairVb != null && _hairIb != null && _hairIndexCount > 0)
        {
            _ctx.OMSetDepthStencilState(_dssRead);
            _ctx.OMSetBlendState(_blendAlpha);
            _ctx.RSSetState(_rsNoCull);
            WriteFrame(viewProj, headModel, cam, blinkAmt, 1.0f);
            BindHairTextures();
            _ctx.PSSetShader(_psHair);
            _ctx.IASetVertexBuffer(0, _hairVb, stride, 0);
            _ctx.IASetIndexBuffer(_hairIb, Format.R32_UInt, 0);
            _ctx.DrawIndexed(_hairIndexCount, 0, 0);
            _ctx.RSSetState(_rs);
        }

        // 4) Rings
        _ctx.OMSetDepthStencilState(_dssRead);
        _ctx.OMSetBlendState(_blendAlpha);
        _ctx.PSSetShader(_psRing);
        _ctx.PSSetShaderResources(0, Array.Empty<ID3D11ShaderResourceView>());
        var ringOpacity = _ringsShowcase ? 1.0f : 1.0f;
        for (var i = 0; i < _rings.Count; i++)
        {
            var r = _rings[i];
            var spinBoost = _ringsShowcase ? 2.2f : 1f;
            var rot = Matrix4x4.CreateFromYawPitchRoll(
                r.BaseYaw + _time * r.SpinY * spinBoost,
                r.BasePitch,
                r.BaseRoll + _time * r.SpinZ * spinBoost);
            var model = Matrix4x4.Transpose(rot * Matrix4x4.CreateTranslation(0, 0.04f, 0.07f));
            WriteFrame(viewProj, model, cam, 0, ringOpacity);
            WriteRing(r.Color, _ringsShowcase ? Math.Min(1f, r.Opacity * 1.15f) : r.Opacity);
            _ctx.IASetVertexBuffer(0, r.Vb, stride, 0);
            _ctx.IASetIndexBuffer(r.Ib, Format.R32_UInt, 0);
            _ctx.DrawIndexed(r.IndexCount, 0, 0);
        }

        _ctx.OMSetDepthStencilState(_dssWrite);
        _ctx.OMSetBlendState(_blendOpaque);

        if (_msaaColor != null)
        {
            using var bb = _swap.GetBuffer<ID3D11Texture2D>(0);
            _ctx.ResolveSubresource(bb, 0, _msaaColor, 0, Format.B8G8R8A8_UNorm);
        }
        _swap.Present(1, PresentFlags.None);
    }

    void BindSkinTextures()
    {
        if (_srvAlbedo == null || _srvNormal == null || _srvOrm == null) return;
        _ctx!.PSSetShaderResources(0, new[] { _srvAlbedo, _srvNormal, _srvOrm });
    }

    void BindHairTextures()
    {
        if (_srvOpacity == null || _srvAlbedo == null) return;
        _ctx!.PSSetShaderResources(0, new[] { _srvOpacity, _srvAlbedo });
    }

    void UpdateBlink(float dt)
    {
        if (_freezeBlink) { _blinkPhase = 0; return; }
        if (_blinkPhase <= 0f && _time >= _nextBlink)
        {
            _blinkPhase = 0.001f;
            _nextBlink = _time + 2.8f + Random.Shared.NextSingle() * 3.2f;
        }
        if (_blinkPhase > 0f)
        {
            _blinkPhase += dt / 0.15f;
            if (_blinkPhase >= 1f) _blinkPhase = 0f;
        }
    }

    float BlinkLidAmount()
    {
        if (_blinkPhase <= 0f) return 0f;
        var t = _blinkPhase;
        float raw = t < 0.35f ? t / 0.35f
            : t < 0.55f ? 1f
            : 1f - (t - 0.55f) / 0.45f;
        return Math.Clamp(raw * raw * (3f - 2f * raw), 0f, 1f);
    }

    void UpdateHeadLook(float dt)
    {
        if (!float.IsNaN(_forcedYaw))
        {
            _headYaw = _forcedYaw;
            _headPitch = 0f;
            return;
        }
        if (_time >= _nextIdleWander)
        {
            _idleYawTarget = (Random.Shared.NextSingle() - 0.5f) * 0.28f;
            _idlePitchTarget = (Random.Shared.NextSingle() - 0.5f) * 0.08f;
            _nextIdleWander = _time + 2.2f + Random.Shared.NextSingle() * 2.8f;
        }
        float wantYaw, wantPitch;
        if (_listening)
        {
            wantYaw = Math.Clamp(_ptrX * 0.42f, -0.35f, 0.35f);
            wantPitch = Math.Clamp(_ptrY * 0.18f, -0.12f, 0.12f);
        }
        else if (MathF.Abs(_ptrX) > 0.08f || MathF.Abs(_ptrY) > 0.08f)
        {
            wantYaw = Math.Clamp(_ptrX * 0.38f + _idleYawTarget * 0.25f, -0.4f, 0.4f);
            wantPitch = Math.Clamp(_ptrY * 0.16f + _idlePitchTarget * 0.35f, -0.14f, 0.14f);
        }
        else
        {
            wantYaw = _idleYawTarget + MathF.Sin(_time * 0.22f) * 0.04f;
            wantPitch = _idlePitchTarget + MathF.Sin(_time * 0.19f) * 0.02f;
        }
        wantYaw += _poseYaw;
        var k = 1f - MathF.Exp(-dt * 3.2f);
        _headYaw = MathHelper.Lerp(_headYaw, wantYaw, k);
        _headPitch = MathHelper.Lerp(_headPitch, wantPitch, k);
    }

    void UpdateGaze(float dt)
    {
        var leadYaw = Math.Clamp((_headYaw - _poseYaw) * 0.35f + _ptrX * 0.12f, -0.22f, 0.22f);
        var leadPitch = Math.Clamp(_headPitch * 0.5f + _ptrY * 0.08f, -0.14f, 0.14f);
        if (_time >= _nextSaccade)
        {
            _saccadeYaw = (Random.Shared.NextSingle() - 0.5f) * 0.09f;
            _saccadePitch = (Random.Shared.NextSingle() - 0.5f) * 0.05f;
            _nextSaccade = _time + 0.9f + Random.Shared.NextSingle() * 1.6f;
        }
        _saccadeYaw = MathHelper.Lerp(_saccadeYaw, 0, 1f - MathF.Exp(-dt * 3.2f));
        _saccadePitch = MathHelper.Lerp(_saccadePitch, 0, 1f - MathF.Exp(-dt * 3.2f));
        _gazeYaw = MathHelper.Lerp(_gazeYaw, leadYaw + _saccadeYaw, 1f - MathF.Exp(-dt * 8.5f));
        _gazePitch = MathHelper.Lerp(_gazePitch, leadPitch + _saccadePitch, 1f - MathF.Exp(-dt * 8.5f));
    }

    void DrawEyes(Matrix4x4 viewProj, Matrix4x4 headWorld, Vector3 cam, float blinkAmt)
    {
        if (_eyeSphere == null || _lidUpper == null || _lidLower == null) return;
        var stride = (uint)Marshal.SizeOf<Vertex>();
        var close = _freezeBlink ? 0f : BlinkLidAmount();

        void DrawOne(Vector3 eyeLocal, bool left)
        {
            var center = eyeLocal + new Vector3(0, 0f, -EyeRadius * 0.35f);
            var converge = left ? 0.018f : -0.018f;
            var eyeRot = Matrix4x4.CreateFromYawPitchRoll(_gazeYaw + converge, _gazePitch, 0);

            _ctx!.OMSetDepthStencilState(_dssWrite);
            _ctx.OMSetBlendState(_blendOpaque);
            _ctx.PSSetShaderResources(0, Array.Empty<ID3D11ShaderResourceView>());

            var eyeWorld =
                Matrix4x4.CreateScale(EyeRadius) *
                eyeRot *
                Matrix4x4.CreateTranslation(center) *
                headWorld;

            WriteFrame(viewProj, Matrix4x4.Transpose(eyeWorld), cam, blinkAmt, 1.05f);
            _ctx.PSSetShader(_psEye);
            _ctx.IASetVertexBuffer(0, _eyeSphere.Vb, stride, 0);
            _ctx.IASetIndexBuffer(_eyeSphere.Ib, Format.R32_UInt, 0);
            _ctx.DrawIndexed(_eyeSphere.IndexCount, 0, 0);

            if (_irisDisc != null)
            {
                var irisWorld =
                    Matrix4x4.CreateScale(EyeRadius * 0.58f) *
                    Matrix4x4.CreateTranslation(0, 0, EyeRadius * 0.82f) *
                    eyeRot *
                    Matrix4x4.CreateTranslation(center) *
                    headWorld;
                WriteFrame(viewProj, Matrix4x4.Transpose(irisWorld), cam, blinkAmt, 1.25f);
                _ctx.PSSetShader(_psIris);
                _ctx.IASetVertexBuffer(0, _irisDisc.Vb, stride, 0);
                _ctx.IASetIndexBuffer(_irisDisc.Ib, Format.R32_UInt, 0);
                _ctx.DrawIndexed(_irisDisc.IndexCount, 0, 0);
            }

            if (_corneaShell != null)
            {
                _ctx.OMSetDepthStencilState(_dssRead);
                _ctx.OMSetBlendState(_blendAlpha);
                var corneaWorld =
                    Matrix4x4.CreateScale(EyeRadius * 1.06f) *
                    eyeRot *
                    Matrix4x4.CreateTranslation(center) *
                    headWorld;
                WriteFrame(viewProj, Matrix4x4.Transpose(corneaWorld), cam, blinkAmt, 1.1f);
                _ctx.PSSetShader(_psCornea);
                _ctx.IASetVertexBuffer(0, _corneaShell.Vb, stride, 0);
                _ctx.IASetIndexBuffer(_corneaShell.Ib, Format.R32_UInt, 0);
                _ctx.DrawIndexed(_corneaShell.IndexCount, 0, 0);
                _ctx.OMSetDepthStencilState(_dssWrite);
                _ctx.OMSetBlendState(_blendOpaque);
            }

            var upperAngle = MathHelper.Lerp(-0.38f, 0.58f, close);
            var upperWorld =
                Matrix4x4.CreateScale(EyeRadius * 1.18f, EyeRadius * 1.12f, EyeRadius * 1.18f) *
                Matrix4x4.CreateRotationX(upperAngle) *
                Matrix4x4.CreateTranslation(center) *
                headWorld;
            WriteFrame(viewProj, Matrix4x4.Transpose(upperWorld), cam, blinkAmt, 1.0f);
            _ctx.PSSetShader(_psLid);
            _ctx.IASetVertexBuffer(0, _lidUpper.Vb, stride, 0);
            _ctx.IASetIndexBuffer(_lidUpper.Ib, Format.R32_UInt, 0);
            _ctx.DrawIndexed(_lidUpper.IndexCount, 0, 0);

            var lowerAngle = MathHelper.Lerp(0.36f, -0.05f, close);
            var lowerWorld =
                Matrix4x4.CreateScale(EyeRadius * 1.16f, EyeRadius * 1.10f, EyeRadius * 1.16f) *
                Matrix4x4.CreateRotationX(lowerAngle) *
                Matrix4x4.CreateTranslation(center) *
                headWorld;
            WriteFrame(viewProj, Matrix4x4.Transpose(lowerWorld), cam, blinkAmt, 1.0f);
            _ctx.IASetVertexBuffer(0, _lidLower.Vb, stride, 0);
            _ctx.IASetIndexBuffer(_lidLower.Ib, Format.R32_UInt, 0);
            _ctx.DrawIndexed(_lidLower.IndexCount, 0, 0);
        }

        DrawOne(_eyeL, left: true);
        DrawOne(_eyeR, left: false);
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
                    Uv = new Vector2(_mesh.UVs[i * 2], _mesh.UVs[i * 2 + 1]),
                };
            }
        }
        _ctx.Unmap(_headVb);
    }

    void BuildEyes()
    {
        _eyeSphere = CreateSphere(1f, 36, 28);
        _irisDisc = CreateIrisDisc(48);
        _corneaShell = CreateSphere(1f, 24, 18);
        _lidUpper = CreateLidShell(upper: true);
        _lidLower = CreateLidShell(upper: false);
    }

    PartMesh CreateIrisDisc(int segments)
    {
        const float dish = 0.12f;
        var verts = new List<Vertex>
        {
            new() { Pos = new Vector3(0, 0, -dish), Nrm = Vector3.UnitZ, Uv = new Vector2(0.5f, 0.5f) },
        };
        for (var i = 0; i <= segments; i++)
        {
            var a = i / (float)segments * MathF.PI * 2;
            var x = MathF.Cos(a);
            var y = MathF.Sin(a);
            var nrm = Vector3.Normalize(new Vector3(-x * 0.35f, -y * 0.35f, 1f));
            verts.Add(new Vertex { Pos = new Vector3(x, y, 0), Nrm = nrm, Uv = new Vector2(x * 0.5f + 0.5f, y * 0.5f + 0.5f) });
        }
        var indices = new List<int>();
        for (var i = 1; i <= segments; i++)
        {
            indices.Add(0); indices.Add(i); indices.Add(i + 1);
        }
        var vb = _device!.CreateBuffer(verts.ToArray(), BindFlags.VertexBuffer);
        var ib = _device.CreateBuffer(indices.ToArray(), BindFlags.IndexBuffer);
        return new PartMesh(vb, ib, (uint)indices.Count);
    }

    PartMesh CreateSphere(float radius, int slices, int stacks)
    {
        var verts = new List<Vertex>();
        var indices = new List<int>();
        for (var i = 0; i <= stacks; i++)
        {
            var v = i / (float)stacks;
            var phi = v * MathF.PI;
            for (var j = 0; j <= slices; j++)
            {
                var u = j / (float)slices;
                var theta = u * MathF.PI * 2;
                var x = MathF.Sin(phi) * MathF.Cos(theta);
                var y = MathF.Cos(phi);
                var z = MathF.Sin(phi) * MathF.Sin(theta);
                var pos = new Vector3(x, y, z) * radius;
                verts.Add(new Vertex
                {
                    Pos = pos,
                    Nrm = Vector3.Normalize(pos),
                    Uv = new Vector2(u, v),
                });
            }
        }
        for (var i = 0; i < stacks; i++)
        for (var j = 0; j < slices; j++)
        {
            var a = i * (slices + 1) + j;
            var b = a + slices + 1;
            indices.Add(a); indices.Add(a + 1); indices.Add(b);
            indices.Add(a + 1); indices.Add(b + 1); indices.Add(b);
        }
        var vb = _device!.CreateBuffer(verts.ToArray(), BindFlags.VertexBuffer);
        var ib = _device.CreateBuffer(indices.ToArray(), BindFlags.IndexBuffer);
        return new PartMesh(vb, ib, (uint)indices.Count);
    }

    PartMesh CreateLidShell(bool upper)
    {
        const int segU = 28, segV = 12;
        var verts = new List<Vertex>();
        var indices = new List<int>();
        for (var i = 0; i <= segV; i++)
        {
            var vv = i / (float)segV;
            float y0, y1;
            if (upper) { y0 = 0.08f; y1 = 1.05f; }
            else { y0 = -1.05f; y1 = -0.08f; }
            var y = MathHelper.Lerp(y0, y1, vv);
            for (var j = 0; j <= segU; j++)
            {
                var uu = j / (float)segU;
                var xWide = MathHelper.Lerp(0.92f, 1.05f, vv);
                var x = MathHelper.Lerp(-xWide, xWide, uu);
                var r2 = x * x + y * y;
                var z = MathF.Sqrt(MathF.Max(0.02f, 1.12f - r2));
                var pos = Vector3.Normalize(new Vector3(x, y, z)) * 1.05f;
                verts.Add(new Vertex { Pos = pos, Nrm = Vector3.Normalize(pos), Uv = new Vector2(uu, vv) });
            }
        }
        for (var i = 0; i < segV; i++)
        for (var j = 0; j < segU; j++)
        {
            var a = i * (segU + 1) + j;
            var b = a + segU + 1;
            indices.Add(a); indices.Add(a + 1); indices.Add(b);
            indices.Add(a + 1); indices.Add(b + 1); indices.Add(b);
        }
        var vb = _device!.CreateBuffer(verts.ToArray(), BindFlags.VertexBuffer);
        var ib = _device.CreateBuffer(indices.ToArray(), BindFlags.IndexBuffer);
        return new PartMesh(vb, ib, (uint)indices.Count);
    }

    void BuildRings()
    {
        void Add(float radius, float tube, float pitchDeg, float yaw, float roll, float spinZ, float spinY = 0.015f, float opacity = 0.85f)
        {
            var pitch = pitchDeg * MathF.PI / 180f;
            var (vb, ib, count) = CreateTorus(radius, tube, MathF.PI * 2);
            _rings.Add(new RingMesh(vb, ib, count, Amber, opacity, pitch, yaw, roll, spinZ, spinY));
        }
        Add(0.42f, 0.0018f, 15f, 0.02f, 0.05f, 0.12f, 0.01f, 0.88f);
        Add(0.48f, 0.0015f, -35f, 0.08f, -0.1f, -0.09f, 0.014f, 0.75f);
        Add(0.54f, 0.0012f, 70f, -0.05f, 0.12f, 0.06f, 0.008f, 0.62f);
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
                    tube * MathF.Sin(v),
                    (radius + tube * MathF.Cos(v)) * sx);
                var nrm = Vector3.Normalize(new Vector3(MathF.Cos(v) * cx, MathF.Sin(v), MathF.Cos(v) * sx));
                verts.Add(new Vertex { Pos = pos, Nrm = nrm, Uv = new Vector2(u / (MathF.PI * 2), v / (MathF.PI * 2)) });
            }
        }
        for (var i = 0; i < segs; i++)
        for (var j = 0; j < sides; j++)
        {
            var a = i * (sides + 1) + j;
            var b = a + sides + 1;
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
        _eyeSphere?.Dispose();
        _irisDisc?.Dispose();
        _corneaShell?.Dispose();
        _lidUpper?.Dispose();
        _lidLower?.Dispose();
        _headVb?.Dispose();
        _headIb?.Dispose();
        _hairVb?.Dispose();
        _hairIb?.Dispose();
        _srvAlbedo?.Dispose();
        _srvNormal?.Dispose();
        _srvOrm?.Dispose();
        _srvOpacity?.Dispose();
        _samplerLinearWrap?.Dispose();
        _frameCb?.Dispose();
        _ringCb?.Dispose();
        _rtvMsaa?.Dispose();
        _dsvMsaa?.Dispose();
        _msaaColor?.Dispose();
        _msaaDepth?.Dispose();
        _rtvResolve?.Dispose();
        _swap?.Dispose();
        _vs?.Dispose();
        _psSkin?.Dispose();
        _psHair?.Dispose();
        _psEye?.Dispose();
        _psIris?.Dispose();
        _psCornea?.Dispose();
        _psLid?.Dispose();
        _psRing?.Dispose();
        _layout?.Dispose();
        _dssWrite?.Dispose();
        _dssRead?.Dispose();
        _blendOpaque?.Dispose();
        _blendAlpha?.Dispose();
        _rs?.Dispose();
        _rsNoCull?.Dispose();
        _ctx?.Dispose();
        _device?.Dispose();
        _ready = false;
    }

    sealed class PartMesh : IDisposable
    {
        public ID3D11Buffer Vb, Ib;
        public uint IndexCount;
        public PartMesh(ID3D11Buffer vb, ID3D11Buffer ib, uint count) { Vb = vb; Ib = ib; IndexCount = count; }
        public void Dispose() { Vb.Dispose(); Ib.Dispose(); }
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

static class MathHelper
{
    public static float Lerp(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0f, 1f);
}