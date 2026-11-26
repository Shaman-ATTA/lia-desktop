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
struct FxaaCB
{
    public Vector2 InvSize;
    public float Time; // also drives PixelHudArcs spin
    public float Pad;
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
    static readonly Vector3 Amber = new(1f, 0.62f, 0.32f);
    // Keep eyeballs inside Rocketbox lids (mesh already has eyelids + blink shapes)
    const float EyeRadius = 0.0115f;
    // Head local Y≈0.20..0.55 → world span ≈0.95; cam Z≈2.82 / fov 24° → head+neck ≈75–80% height
    const float HeadScale = 2.70f;
    const float HeadTransY = -1.00f;
    const int MsaaCount = 2; // 4x was ~2× CPU on WARP; 2x still cleans edges
    int _msaaSamples = 1;

    ID3D11Device? _device;
    ID3D11DeviceContext? _ctx;
    IDXGISwapChain1? _swap;
    ID3D11Texture2D? _msaaColor;
    ID3D11Texture2D? _msaaDepth;
    ID3D11Texture2D? _sceneColor;
    ID3D11RenderTargetView? _rtvMsaa;
    ID3D11DepthStencilView? _dsvMsaa;
    ID3D11RenderTargetView? _rtvScene;
    ID3D11ShaderResourceView? _srvScene;
    ID3D11RenderTargetView? _rtvSwap;
    ID3D11Buffer? _frameCb;
    ID3D11Buffer? _ringCb;
    ID3D11Buffer? _fxaaCb;
    ID3D11Buffer? _fsVb; // fullscreen triangle for FXAA
    ID3D11InputLayout? _fsLayout;
    ID3D11Buffer? _headVb;
    ID3D11Buffer? _headIb;
    ID3D11Buffer? _hairVb;
    ID3D11Buffer? _hairIb;
    uint _hairIndexCount;
    ID3D11VertexShader? _vs;
    ID3D11VertexShader? _vsFxaa;
    ID3D11PixelShader? _psSkin;
    ID3D11PixelShader? _psHair;
    ID3D11PixelShader? _psEye;
    ID3D11PixelShader? _psIris;
    ID3D11PixelShader? _psCornea;
    ID3D11PixelShader? _psLid;
    ID3D11PixelShader? _psRing;
    ID3D11PixelShader? _psHudArcs;
    ID3D11PixelShader? _psFxaa;
    ID3D11InputLayout? _layout;
    ID3D11DepthStencilState? _dssWrite;
    ID3D11DepthStencilState? _dssRead;
    ID3D11DepthStencilState? _dssEqual;
    ID3D11DepthStencilState? _dssOff;
    ID3D11BlendState? _blendOpaque;
    ID3D11BlendState? _blendAlpha;
    ID3D11BlendState? _blendPremul;
    ID3D11BlendState? _blendNoColor; // depth prepass: write depth, no color
    ID3D11RasterizerState? _rs;
    ID3D11RasterizerState? _rsNoCull;
    ID3D11SamplerState? _samplerLinearWrap;
    ID3D11SamplerState? _samplerLinearClamp;
    ID3D11ShaderResourceView? _srvAlbedo;
    ID3D11ShaderResourceView? _srvNormal;
    ID3D11ShaderResourceView? _srvOrm;
    ID3D11ShaderResourceView? _srvOpacity;
    ID3D11ShaderResourceView? _srvFallback;

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
        _vsFxaa = CompileVS(HoloShaders.VertexFxaa, out var fxaaBlob);
        _psSkin = CompilePS(HoloShaders.PixelSkin);
        _psHair = CompilePS(HoloShaders.PixelHair);
        _psEye = CompilePS(HoloShaders.PixelEye);
        _psIris = CompilePS(HoloShaders.PixelIris);
        _psCornea = CompilePS(HoloShaders.PixelCornea);
        _psLid = CompilePS(HoloShaders.PixelLid);
        _psRing = CompilePS(HoloShaders.PixelRing);
        _psHudArcs = CompilePS(HoloShaders.PixelHudArcs);
        _psFxaa = CompilePS(HoloShaders.PixelFxaa);
        _layout = _device.CreateInputLayout(new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 24, 0),
        }, vsBlob.Span);
        _fsLayout = _device.CreateInputLayout(new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 8, 0),
        }, fxaaBlob.Span);
        // Fullscreen triangle in clip space (pos.xy + uv)
        _fsVb = _device.CreateBuffer(new FsVert[]
        {
            new(-1f, -1f, 0f, 1f),
            new(-1f,  3f, 0f, -1f),
            new( 3f, -1f, 2f, 1f),
        }, BindFlags.VertexBuffer);

        _frameCb = _device.CreateBuffer(new BufferDescription(
            (uint)Marshal.SizeOf<FrameCB>(), BindFlags.ConstantBuffer,
            ResourceUsage.Dynamic, CpuAccessFlags.Write, ResourceOptionFlags.None, 0));
        _ringCb = _device.CreateBuffer(new BufferDescription(
            16, BindFlags.ConstantBuffer,
            ResourceUsage.Dynamic, CpuAccessFlags.Write, ResourceOptionFlags.None, 0));
        _fxaaCb = _device.CreateBuffer(new BufferDescription(
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
        _dssEqual = _device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.Equal,
        });
        _dssOff = _device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = false,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.Always,
        });
        _blendOpaque = _device.CreateBlendState(BlendDescription.Opaque);
        _blendAlpha = _device.CreateBlendState(BlendDescription.AlphaBlend);
        var premulDesc = new BlendDescription
        {
            AlphaToCoverageEnable = false,
            IndependentBlendEnable = false,
        };
        premulDesc.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = Blend.One,
            DestinationBlend = Blend.InverseSourceAlpha,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One,
            DestinationBlendAlpha = Blend.InverseSourceAlpha,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };
        _blendPremul = _device.CreateBlendState(premulDesc);
        var noColorDesc = new BlendDescription
        {
            AlphaToCoverageEnable = false,
            IndependentBlendEnable = false,
        };
        noColorDesc.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = false,
            RenderTargetWriteMask = 0, // depth prepass — no color
        };
        _blendNoColor = _device.CreateBlendState(noColorDesc);
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
        _samplerLinearClamp = _device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MaxAnisotropy = 1,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD = 0,
            MaxLOD = float.MaxValue,
        });

        BuildRings();
        BuildEyes();
        Program.BootLog($"InitDevice ok rings={_rings.Count} eye={_eyeSphere != null}");
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

        try
        {
            _srvAlbedo = TryLoadTexturePng(Path.Combine(dir, "lia_pbr_albedo.png"));
            _srvNormal = TryLoadTexturePng(Path.Combine(dir, "lia_pbr_normal.png"));
            _srvOrm = TryLoadTexturePng(Path.Combine(dir, "lia_pbr_orm.png"));
            _srvOpacity = TryLoadTexturePng(Path.Combine(dir, "lia_pbr_opacity.png"));
            Program.BootLog($"pbr tex albedo={_srvAlbedo != null} nrm={_srvNormal != null} orm={_srvOrm != null} op={_srvOpacity != null} verts={_mesh.VertexCount}");
        }
        catch (Exception ex)
        {
            Program.BootLog("texture load FATAL: " + ex);
            throw;
        }
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
        _rtvScene?.Dispose();
        _srvScene?.Dispose();
        _sceneColor?.Dispose();
        _rtvSwap?.Dispose();
        _swap = swap;

        using var bb = swap.GetBuffer<ID3D11Texture2D>(0);
        _rtvSwap = _device.CreateRenderTargetView(bb);

        // Scene color (non-MSAA) — resolve target + FXAA source
        _sceneColor = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        _rtvScene = _device.CreateRenderTargetView(_sceneColor);
        _srvScene = _device.CreateShaderResourceView(_sceneColor);

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
        _msaaSamples = (int)sample.Count;
        Program.BootLog($"RT {_width}x{_height} msaa={_msaaSamples} fxaa=1");
    }

    public IDXGIFactory2 GetDxgiFactory()
    {
        using var dxgi = _device!.QueryInterface<IDXGIDevice>();
        using var adapter = dxgi.GetAdapter();
        return adapter.GetParent<IDXGIFactory2>();
    }

    public void Tick(float dt)
    {
        if (!_ready || _device == null || _ctx == null || _rtvMsaa == null || _dsvMsaa == null ||
            _swap == null || _rtvScene == null || _rtvSwap == null || _srvScene == null)
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
            ? 0.06f + 0.03f * MathF.Sin(_time * 0.35f)
            : 0.04f;
        var brow = float.IsNaN(_forcedJaw)
            ? 0.08f + 0.05f * MathF.Sin(_time * 0.41f + 1.2f)
            : 0.05f;

        if (_mesh != null && _morphScratch != null)
        {
            var lid = BlinkLidAmount();
            _mesh.Morph(Math.Min(0.62f, jaw * 0.88f), funnel * 0.50f, wide * 0.50f, smile, brow, lid, _morphScratch);
            UploadHead(_morphScratch);
        }

        // Scene → MSAA RT (or scene color when MSAA unavailable); FXAA to swapchain after.
        var drawRtv = _msaaSamples > 1 ? _rtvMsaa! : _rtvScene!;
        var drawDsv = _dsvMsaa!;
        _ctx.OMSetRenderTargets(drawRtv, drawDsv);
        _ctx.RSSetViewport(new Viewport(0, 0, _width, _height));
        // v0.6.2 opaque navy clear — stage composites reliably; arcs drawn in-pass behind head.
        _ctx.ClearRenderTargetView(drawRtv, new Color4(0.015f, 0.012f, 0.03f, 1f));
        _ctx.ClearDepthStencilView(drawDsv, DepthStencilClearFlags.Depth, 1, 0);
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
        // Eye local ≈ (±0.032, 0.43, 0.05) → world after HeadScale/Trans
        var eyeY = 0.43f * HeadScale + HeadTransY; // ≈ 0.16
        if (_eyesCloseup)
        {
            // Both eyes (outer-corner span) ≈60% of viewport width
            lookAt = new Vector3(0f, eyeY, 0.06f);
            cam = new Vector3(0f, eyeY, 1.18f);
            fovDeg = 16f;
            nearPlane = 0.08f;
        }
        else if (_ringsShowcase)
        {
            var orbit = MathF.Sin(_time * 0.14f) * 0.06f;
            // Exact v0.6.2 framing baseline
            cam = new Vector3(orbit, 0.00f, 2.82f);
            lookAt = new Vector3(0, 0.00f, 0.02f);
            fovDeg = 24f;
        }
        else
        {
            // Exact v0.6.2 framing — head+neck ≈75–80% viewport height
            var orbit = MathF.Sin(_time * 0.09f) * 0.04f;
            cam = new Vector3(orbit, 0.00f, 2.82f);
            lookAt = new Vector3(0, 0.00f, 0.02f);
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

        // 0) Dense 2D arcs FIRST (screen-space) — head draws after and occludes them
        if (!_eyesCloseup)
            DrawBackgroundArcs();

        // Restore mesh IA after fullscreen pass
        _ctx.IASetInputLayout(_layout);
        _ctx.VSSetShader(_vs);
        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        if (_samplerLinearWrap != null)
            _ctx.PSSetSamplers(0, new[] { _samplerLinearWrap });
        _ctx.VSSetConstantBuffer(0, _frameCb);
        _ctx.PSSetConstantBuffer(0, _frameCb);

        // 1) Skin — opaque depth prepass, then premul color with depth-equal.
        //    Stops back-face neck triangles showing through as grey jaw blotches.
        WriteFrame(viewProj, headModel, cam, blinkAmt, 1.0f);
        BindSkinTextures();
        _ctx.PSSetShader(_psSkin);
        if (_headVb != null && _headIb != null && _mesh != null)
        {
            _ctx.IASetVertexBuffer(0, _headVb, stride, 0);
            _ctx.IASetIndexBuffer(_headIb, Format.R32_UInt, 0);
            // 1a) Depth only (front faces) — establishes silhouette depth
            _ctx.OMSetDepthStencilState(_dssWrite);
            _ctx.OMSetBlendState(_blendNoColor!);
            _ctx.RSSetState(_rs); // cull back
            _ctx.DrawIndexed((uint)_mesh.Indices.Length, 0, 0);
            // 1b) Color premul, depth test on / write off (LessEqual — MSAA-safe).
            //    Equal fails under MSAA and blacked-out the face; LessEqual still
            //    rejects farther back-faces so jaw blotches stay gone.
            _ctx.OMSetDepthStencilState(_dssRead);
            _ctx.OMSetBlendState(_blendPremul!, new Color4(0, 0, 0, 1), 0xFFFFFFFF);
            _ctx.RSSetState(_rs); // cull back — match prepass faces
            _ctx.DrawIndexed((uint)_mesh.Indices.Length, 0, 0);
        }
        _ctx.RSSetState(_rs);

        // 2) Eyes + cornea
        DrawEyes(viewProj, headWorld, cam, blinkAmt);

        // 3) Hair — alpha-tested dark cards (lashes clipped; no amber holo on cards)
        if (_hairVb != null && _hairIb != null && _hairIndexCount > 0)
        {
            _ctx.OMSetDepthStencilState(_dssRead);
            _ctx.OMSetBlendState(_blendOpaque); // opaque; neck stump is skin-only fade
            _ctx.RSSetState(_rsNoCull);
            WriteFrame(viewProj, headModel, cam, blinkAmt, 1.0f);
            BindHairTextures();
            _ctx.PSSetShader(_psHair);
            _ctx.IASetVertexBuffer(0, _hairVb, stride, 0);
            _ctx.IASetIndexBuffer(_hairIb, Format.R32_UInt, 0);
            _ctx.DrawIndexed(_hairIndexCount, 0, 0);
            _ctx.RSSetState(_rs);
        }

        // 4) Rings — parented to head orient (so yaw never slices the face);
        //    placed behind face plane in head space; hidden in eyes close-up
        if (!_eyesCloseup)
        {
            var headOrient = Matrix4x4.CreateRotationX(pitch)
                             * Matrix4x4.CreateRotationY(yaw)
                             * Matrix4x4.CreateTranslation(0, HeadTransY + breathY, 0);
            _ctx.OMSetDepthStencilState(_dssRead);
            _ctx.OMSetBlendState(_blendPremul);
            _ctx.RSSetState(_rsNoCull);
            _ctx.PSSetShader(_psRing);
            _ctx.PSSetShaderResources(0, Array.Empty<ID3D11ShaderResourceView>());
            for (var i = 0; i < _rings.Count; i++)
            {
                var r = _rings[i];
                var spinBoost = _ringsShowcase ? 2.2f : 1f;
                var rot = Matrix4x4.CreateFromYawPitchRoll(
                    r.BaseYaw + _time * r.SpinY * spinBoost,
                    r.BasePitch,
                    r.BaseRoll);
                var ringLocal = rot * Matrix4x4.CreateTranslation(0f, r.TransY, r.TransZ);
                var model = Matrix4x4.Transpose(ringLocal * headOrient);
                WriteFrame(viewProj, model, cam, 0, 1f);
                WriteRing(r.Color, _ringsShowcase ? Math.Min(0.85f, r.Opacity * 1.2f) : r.Opacity);
                _ctx.IASetVertexBuffer(0, r.Vb, stride, 0);
                _ctx.IASetIndexBuffer(r.Ib, Format.R32_UInt, 0);
                _ctx.DrawIndexed(r.IndexCount, 0, 0);
            }
            _ctx.RSSetState(_rs);
        }

        _ctx.OMSetDepthStencilState(_dssWrite);
        _ctx.OMSetBlendState(_blendOpaque);

        // Unbind RTs before resolve/copy (MSAA DSV must not stay bound with 1x swap)
        _ctx.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>(), null);

        // Resolve into scene color
        if (_msaaSamples > 1 && _msaaColor != null && _sceneColor != null)
            _ctx.ResolveSubresource(_sceneColor, 0, _msaaColor, 0, Format.B8G8R8A8_UNorm);
        // msaa==1 already drew into _rtvScene

        // Blit scene → swap. FXAA only when MSAA unavailable (CI/WARP often has MSAA=4).
        if (_msaaSamples <= 1)
        {
            if (!ApplyFxaa())
            {
                using var bb = _swap.GetBuffer<ID3D11Texture2D>(0);
                if (_sceneColor != null)
                    _ctx.CopyResource(bb, _sceneColor);
            }
        }
        else
        {
            using var bb = _swap.GetBuffer<ID3D11Texture2D>(0);
            if (_sceneColor != null)
                _ctx.CopyResource(bb, _sceneColor);
        }
        // SyncInterval 2 ≈ 30Hz — halves GPU/WARP load vs every-vsync Present
        _swap.Present(2, PresentFlags.None);
    }

    void DrawBackgroundArcs()
    {
        if (_ctx == null || _psHudArcs == null || _vsFxaa == null || _fxaaCb == null ||
            _fsVb == null || _fsLayout == null)
            return;

        // Depth OFF — arcs are a background plate. NoCull: FS triangle is CW and
        // would be backface-culled by _rs (FrontCounterClockwise + CullBack).
        _ctx.OMSetDepthStencilState(_dssOff);
        _ctx.OMSetBlendState(_blendPremul!, new Color4(0, 0, 0, 1), 0xFFFFFFFF);
        _ctx.RSSetState(_rsNoCull);
        _ctx.IASetInputLayout(_fsLayout);
        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _ctx.IASetVertexBuffer(0, _fsVb, 16, 0);
        _ctx.VSSetShader(_vsFxaa);
        _ctx.PSSetShader(_psHudArcs);
        _ctx.VSSetConstantBuffer(0, _fxaaCb);
        _ctx.PSSetConstantBuffer(0, _fxaaCb);
        _ctx.PSSetShaderResources(0, Array.Empty<ID3D11ShaderResourceView>());

        var mapped = _ctx.Map(_fxaaCb, MapMode.WriteDiscard, MapFlags.None);
        unsafe
        {
            *(FxaaCB*)mapped.DataPointer = new FxaaCB
            {
                InvSize = new Vector2(1f / Math.Max(1, _width), 1f / Math.Max(1, _height)),
                Time = _time,
            };
        }
        _ctx.Unmap(_fxaaCb, 0);
        _ctx.Draw(3, 0);
    }

    bool ApplyFxaa()
    {
        if (_ctx == null || _rtvSwap == null || _srvScene == null || _psFxaa == null || _vsFxaa == null ||
            _fxaaCb == null || _sceneColor == null || _fsVb == null || _fsLayout == null)
            return false;

        try
        {
            _ctx.OMSetRenderTargets(new[] { _rtvSwap }, null);
            _ctx.OMSetDepthStencilState(_dssOff);
            _ctx.OMSetBlendState(_blendOpaque);
            _ctx.RSSetViewport(new Viewport(0, 0, _width, _height));
            _ctx.RSSetState(_rsNoCull); // FS triangle is CW — avoid backface cull
            _ctx.IASetInputLayout(_fsLayout);
            _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _ctx.IASetVertexBuffer(0, _fsVb, 16, 0);
            _ctx.VSSetShader(_vsFxaa);
            _ctx.PSSetShader(_psFxaa);
            _ctx.VSSetConstantBuffer(0, _fxaaCb);
            _ctx.PSSetConstantBuffer(0, _fxaaCb);
            _ctx.PSSetShaderResources(0, new[] { _srvScene });
            if (_samplerLinearClamp != null)
                _ctx.PSSetSamplers(0, new[] { _samplerLinearClamp });

            var mapped = _ctx.Map(_fxaaCb, MapMode.WriteDiscard, MapFlags.None);
            unsafe
            {
                *(FxaaCB*)mapped.DataPointer = new FxaaCB
                {
                    InvSize = new Vector2(1f / Math.Max(1, _width), 1f / Math.Max(1, _height)),
                    Time = _time,
                };
            }
            _ctx.Unmap(_fxaaCb, 0);
            _ctx.Draw(3, 0);
        }
        finally
        {
            _ctx.PSSetShaderResources(0, Array.Empty<ID3D11ShaderResourceView>());
            _ctx.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>(), null);
            _ctx.VSSetShader(_vs);
            _ctx.IASetInputLayout(_layout);
            if (_samplerLinearWrap != null)
                _ctx.PSSetSamplers(0, new[] { _samplerLinearWrap });
            _ctx.VSSetConstantBuffer(0, _frameCb);
            _ctx.PSSetConstantBuffer(0, _frameCb);
        }
        return true;
    }

    void BindSkinTextures()
    {
        EnsureFallbackTex();
        _ctx!.PSSetShaderResources(0, new[] {
            _srvAlbedo ?? _srvFallback!,
            _srvNormal ?? _srvFallback!,
            _srvOrm ?? _srvFallback!,
        });
    }

    void BindHairTextures()
    {
        EnsureFallbackTex();
        _ctx!.PSSetShaderResources(0, new[] {
            _srvOpacity ?? _srvFallback!,
            _srvAlbedo ?? _srvFallback!,
        });
    }

    void EnsureFallbackTex()
    {
        if (_srvFallback != null || _device == null) return;
        var desc = new Texture2DDescription
        {
            Width = 1, Height = 1, MipLevels = 1, ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Immutable,
            BindFlags = BindFlags.ShaderResource,
        };
        // warm skin-ish pixel
        byte[] px = [200, 150, 120, 255];
        unsafe
        {
            fixed (byte* p = px)
            {
                var init = new SubresourceData(p, 4, 4);
                var tex = _device.CreateTexture2D(desc, init);
                _srvFallback = _device.CreateShaderResourceView(tex);
            }
        }
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
        if (_eyeSphere == null) return;
        var stride = (uint)Marshal.SizeOf<Vertex>();
        // Procedural lids OFF — Rocketbox head mesh already has lids; blink morph closes them.

        void DrawOne(Vector3 eyeLocal, bool left)
        {
            // Recess into socket so mesh lids occlude the sphere rim
            var center = eyeLocal + new Vector3(0, -0.006f, -0.022f);
            var converge = left ? 0.012f : -0.012f;
            var eyeRot = Matrix4x4.CreateFromYawPitchRoll(_gazeYaw + converge, _gazePitch, 0);

            _ctx!.OMSetDepthStencilState(_dssWrite);
            _ctx.OMSetBlendState(_blendOpaque);
            _ctx.RSSetState(_rs);
            _ctx.PSSetShaderResources(0, Array.Empty<ID3D11ShaderResourceView>());

            var eyeWorld =
                Matrix4x4.CreateScale(EyeRadius) *
                eyeRot *
                Matrix4x4.CreateTranslation(center) *
                headWorld;

            WriteFrame(viewProj, Matrix4x4.Transpose(eyeWorld), cam, blinkAmt, 1.0f);
            _ctx.PSSetShader(_psEye);
            _ctx.IASetVertexBuffer(0, _eyeSphere.Vb, stride, 0);
            _ctx.IASetIndexBuffer(_eyeSphere.Ib, Format.R32_UInt, 0);
            _ctx.DrawIndexed(_eyeSphere.IndexCount, 0, 0);

            if (_irisDisc != null)
            {
                var irisWorld =
                    Matrix4x4.CreateScale(EyeRadius * 0.72f) *
                    Matrix4x4.CreateTranslation(0, 0, EyeRadius * 0.78f) *
                    eyeRot *
                    Matrix4x4.CreateTranslation(center) *
                    headWorld;
                WriteFrame(viewProj, Matrix4x4.Transpose(irisWorld), cam, blinkAmt, 1.1f);
                _ctx.PSSetShader(_psIris);
                _ctx.IASetVertexBuffer(0, _irisDisc.Vb, stride, 0);
                _ctx.IASetIndexBuffer(_irisDisc.Ib, Format.R32_UInt, 0);
                _ctx.DrawIndexed(_irisDisc.IndexCount, 0, 0);
            }

            if (_corneaShell != null)
            {
                _ctx.OMSetDepthStencilState(_dssRead);
                _ctx.OMSetBlendState(_blendAlpha);
                // Slightly larger than iris plane — specular wetness only
                var corneaWorld =
                    Matrix4x4.CreateScale(EyeRadius * 1.02f) *
                    eyeRot *
                    Matrix4x4.CreateTranslation(center) *
                    headWorld;
                WriteFrame(viewProj, Matrix4x4.Transpose(corneaWorld), cam, blinkAmt, 1.0f);
                _ctx.PSSetShader(_psCornea);
                _ctx.IASetVertexBuffer(0, _corneaShell.Vb, stride, 0);
                _ctx.IASetIndexBuffer(_corneaShell.Ib, Format.R32_UInt, 0);
                _ctx.DrawIndexed(_corneaShell.IndexCount, 0, 0);
                _ctx.OMSetDepthStencilState(_dssWrite);
                _ctx.OMSetBlendState(_blendOpaque);
            }
        }

        DrawOne(_eyeL, left: true);
        DrawOne(_eyeR, left: false);
    }

    void WriteFrame(Matrix4x4 vp, Matrix4x4 model, Vector3 cam, float blink, float glow, float neckPass = 0f)
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
            Pad = new Vector2(neckPass, 0f),
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
        // Shallow dome facing +Z (toward camera)
        const float dish = 0.08f;
        var verts = new List<Vertex>
        {
            new() { Pos = new Vector3(0, 0, dish), Nrm = Vector3.UnitZ, Uv = new Vector2(0.5f, 0.5f) },
        };
        for (var i = 0; i <= segments; i++)
        {
            var a = i / (float)segments * MathF.PI * 2;
            var x = MathF.Cos(a);
            var y = MathF.Sin(a);
            var nrm = Vector3.Normalize(new Vector3(x * 0.25f, y * 0.25f, 1f));
            verts.Add(new Vertex { Pos = new Vector3(x, y, 0), Nrm = nrm, Uv = new Vector2(x * 0.5f + 0.5f, y * 0.5f + 0.5f) });
        }
        var indices = new List<int>();
        for (var i = 1; i <= segments; i++)
        {
            // CCW from +Z
            indices.Add(0); indices.Add(i + 1); indices.Add(i);
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
        // v0.6.4: no 3D crown/chin rings — they read as diagonal bars / smeared streaks.
        // Dense 2D HUD arcs (MainWindow) already provide the halo look; keep those as-is.
        _rings.Clear();
    }

    (ID3D11Buffer vb, ID3D11Buffer ib, uint count) CreateTorus(float radius, float tube, float arc, float startU = 0f)
    {
        const int seg = 96, sides = 12;
        var verts = new List<Vertex>();
        var indices = new List<int>();
        var segs = Math.Max(6, (int)(seg * (arc / (MathF.PI * 2))));
        for (var i = 0; i <= segs; i++)
        {
            var u = startU + i / (float)segs * arc;
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
        try
        {
            var result = Compiler.Compile(src, "main", "ps.hlsl", "ps_5_0");
            return _device!.CreatePixelShader(result.Span);
        }
        catch (Exception ex)
        {
            Program.BootLog("CompilePS FATAL: " + ex.Message);
            throw;
        }
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
        _srvFallback?.Dispose();
        _srvScene?.Dispose();
        _samplerLinearWrap?.Dispose();
        _samplerLinearClamp?.Dispose();
        _frameCb?.Dispose();
        _ringCb?.Dispose();
        _fxaaCb?.Dispose();
        _rtvMsaa?.Dispose();
        _dsvMsaa?.Dispose();
        _msaaColor?.Dispose();
        _msaaDepth?.Dispose();
        _rtvScene?.Dispose();
        _sceneColor?.Dispose();
        _rtvSwap?.Dispose();
        _swap?.Dispose();
        _vs?.Dispose();
        _vsFxaa?.Dispose();
        _psSkin?.Dispose();
        _psHair?.Dispose();
        _psEye?.Dispose();
        _psIris?.Dispose();
        _psCornea?.Dispose();
        _psLid?.Dispose();
        _psRing?.Dispose();
        _psHudArcs?.Dispose();
        _psFxaa?.Dispose();
        _layout?.Dispose();
        _fsLayout?.Dispose();
        _fsVb?.Dispose();
        _dssWrite?.Dispose();
        _dssRead?.Dispose();
        _dssEqual?.Dispose();
        _dssOff?.Dispose();
        _blendOpaque?.Dispose();
        _blendAlpha?.Dispose();
        _blendPremul?.Dispose();
        _blendNoColor?.Dispose();
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
        public float Opacity, BasePitch, BaseYaw, BaseRoll, SpinZ, SpinY, TransY, TransZ;
        public RingMesh(ID3D11Buffer vb, ID3D11Buffer ib, uint count, Vector3 c, float o,
            float pitch, float yaw, float roll, float spinZ, float spinY, float transY, float transZ)
        {
            Vb = vb; Ib = ib; IndexCount = count; Color = c; Opacity = o;
            BasePitch = pitch; BaseYaw = yaw; BaseRoll = roll;
            SpinZ = spinZ; SpinY = spinY; TransY = transY; TransZ = transZ;
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
struct FsVert
{
    public float X, Y, U, V;
    public FsVert(float x, float y, float u, float v) { X = x; Y = y; U = u; V = v; }
}

static class MathHelper
{
    public static float Lerp(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0f, 1f);
}