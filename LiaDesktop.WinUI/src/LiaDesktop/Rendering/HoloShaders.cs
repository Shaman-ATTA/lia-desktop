namespace LiaDesktop.Rendering;

/// <summary>HLSL — photoreal PBR skin/eyes/hair with a subtle amber hologram veil + FXAA.</summary>
public static class HoloShaders
{
    public const string Vertex = @"
cbuffer Frame : register(b0) {
  float4x4 viewProj;
  float4x4 model;
  float3 camPos; float time;
  float glow; float blink; float2 pad;
};
struct VSIn { float3 pos:POSITION; float3 nrm:NORMAL; float2 uv:TEXCOORD0; };
struct VSOut {
  float4 pos:SV_POSITION;
  float3 world:TEXCOORD0;
  float3 nrm:TEXCOORD1;
  float2 uv:TEXCOORD2;
  float3 local:TEXCOORD3;
};
VSOut main(VSIn i) {
  VSOut o;
  float4 wp = mul(float4(i.pos,1), model);
  o.world = wp.xyz;
  o.nrm = normalize(mul(i.nrm, (float3x3)model));
  o.uv = i.uv;
  o.local = i.pos;
  o.pos = mul(wp, viewProj);
  return o;
}";

    public const string PixelSkin = @"
cbuffer Frame : register(b0) {
  float4x4 viewProj;
  float4x4 model;
  float3 camPos; float time;
  float glow; float blink; float2 pad;
};
Texture2D albedoMap : register(t0);
Texture2D normalMap : register(t1);
Texture2D ormMap    : register(t2);
SamplerState samp   : register(s0);
struct PSIn {
  float4 pos:SV_POSITION;
  float3 world:TEXCOORD0;
  float3 nrm:TEXCOORD1;
  float2 uv:TEXCOORD2;
  float3 local:TEXCOORD3;
};
float4 main(PSIn i) : SV_TARGET {
  float3 albedo = albedoMap.Sample(samp, i.uv).rgb;
  if (dot(albedo, albedo) < 1e-6) albedo = float3(0.78, 0.55, 0.42);
  albedo = pow(max(albedo, 1e-4), 2.2);
  float3 nm = normalMap.Sample(samp, i.uv).xyz * 2 - 1;
  float3 N = normalize(normalize(i.nrm) + nm * float3(1,1,0.55) * 0.45);
  float3 V = normalize(camPos - i.world);
  float3 L = normalize(float3(0.35, 0.55, 0.75));
  float3 H = normalize(L+V);
  float wrap = saturate((dot(N,L)+0.45)/1.45);
  float ndl = saturate(dot(N,L));
  float rough = ormMap.Sample(samp, i.uv).g;
  float specPow = lerp(96.0, 12.0, rough);
  float spec = pow(saturate(dot(N,H)), specPow) * (1.0 - rough) * 0.55;
  float3 key = float3(1.00, 0.92, 0.82);
  float3 fill = float3(0.45, 0.55, 0.75);
  float3 ambient = float3(0.22, 0.18, 0.16);
  float3 diffuse = albedo * (ambient + key * wrap * 0.95 + fill * (1.0-ndl) * 0.22);
  float sss = saturate(1.0 - abs(i.local.x)*8.0) * 0.12;
  diffuse += albedo * float3(0.85, 0.25, 0.18) * sss;
  float3 color = diffuse + key * spec;

  // Amber hologram veil (~20% tint, thin scanlines, fresnel rim, edge soft-out)
  float ndv = saturate(dot(N,V));
  float fres = pow(1.0 - ndv, 2.4);
  float scan = 0.038 * sin((i.world.y + time*0.14)*130.0);
  float3 amber = float3(1.0, 0.62, 0.28);
  color = lerp(color, color * float3(1.12, 0.88, 0.65), 0.20);
  color += amber * fres * 0.30 * glow;
  color += amber * scan;
  float edgeKeep = lerp(0.50, 1.0, saturate(ndv * 1.35 + 0.02));
  color *= edgeKeep;
  // Neck stump → darkness (start fade under chin)
  float neck = smoothstep(0.24, 0.40, i.local.y);
  color *= neck;

  color = color / (1.0 + color * 0.65);
  color = pow(saturate(color), 1.0/2.2);
  // Opaque — neck already multiplied to black (matches clear color)
  return float4(color, 1.0);
}";

    public const string PixelHair = @"
cbuffer Frame : register(b0) {
  float4x4 viewProj;
  float4x4 model;
  float3 camPos; float time;
  float glow; float blink; float2 pad;
};
Texture2D opacityMap : register(t0);
Texture2D albedoMap  : register(t1);
SamplerState samp    : register(s0);
struct PSIn {
  float4 pos:SV_POSITION;
  float3 world:TEXCOORD0;
  float3 nrm:TEXCOORD1;
  float2 uv:TEXCOORD2;
  float3 local:TEXCOORD3;
};
float4 main(PSIn i) : SV_TARGET {
  float4 op = opacityMap.Sample(samp, i.uv);
  float a = op.a;
  if (a < 0.02) a = max(op.r, max(op.g, op.b));
  // Hard alpha-test — kills glitter fringe on lash/hair cards
  clip(a - 0.50);

  // Always dark — lashes must never read as white/gold sparkle
  float3 hairCol = float3(0.035, 0.022, 0.015);
  float luma = dot(op.rgb, float3(0.299, 0.587, 0.114));
  if (luma < 0.22 && a > 0.75)
    hairCol = lerp(hairCol, op.rgb * 0.35, 0.45);
  hairCol = min(hairCol, float3(0.10, 0.06, 0.04));

  float3 N = normalize(i.nrm);
  float3 L = normalize(float3(0.3, 0.7, 0.6));
  float ndl = saturate(dot(N,L)*0.45+0.55);
  float3 color = hairCol * ndl;
  color = pow(saturate(color), 1.0/2.2);
  return float4(color, 1.0);
}";

    public const string PixelEye = @"
cbuffer Frame : register(b0) {
  float4x4 viewProj;
  float4x4 model;
  float3 camPos; float time;
  float glow; float blink; float2 pad;
};
struct PSIn {
  float4 pos:SV_POSITION;
  float3 world:TEXCOORD0;
  float3 nrm:TEXCOORD1;
  float2 uv:TEXCOORD2;
  float3 local:TEXCOORD3;
};
float4 main(PSIn i) : SV_TARGET {
  float3 N = normalize(i.nrm);
  float3 V = normalize(camPos - i.world);
  float3 L = normalize(float3(0.3, 0.5, 0.8));
  float wrap = saturate((dot(N,L)+0.4)/1.4);
  float3 sclera = float3(0.78, 0.72, 0.68);
  float3 color = sclera * (0.40 + wrap * 0.55);
  float fres = pow(1.0 - saturate(dot(N,V)), 3.0);
  color += float3(1,0.85,0.6) * fres * 0.06;
  color = pow(saturate(color), 1.0/2.2);
  return float4(color, 1);
}";

    public const string PixelIris = @"
cbuffer Frame : register(b0) {
  float4x4 viewProj;
  float4x4 model;
  float3 camPos; float time;
  float glow; float blink; float2 pad;
};
struct PSIn {
  float4 pos:SV_POSITION;
  float3 world:TEXCOORD0;
  float3 nrm:TEXCOORD1;
  float2 uv:TEXCOORD2;
  float3 local:TEXCOORD3;
};
float4 main(PSIn i) : SV_TARGET {
  float2 p = i.uv * 2 - 1;
  float r = length(p);
  clip(0.98 - r);
  float angle = atan2(p.y, p.x);
  float radial = 0.55 + 0.45 * sin(angle * 14.0 + r * 9.0);
  float3 iris = lerp(float3(0.12, 0.18, 0.08), float3(0.42, 0.48, 0.22), radial);
  iris = lerp(iris, float3(0.55, 0.52, 0.28), saturate(1.0 - r * 1.2) * 0.35);
  float pupil = smoothstep(0.28, 0.18, r);
  iris = lerp(iris, float3(0.02, 0.01, 0.01), pupil);
  iris *= smoothstep(1.0, 0.72, r);
  float3 V = normalize(camPos - i.world);
  float3 N = normalize(i.nrm);
  float3 L = normalize(float3(0.45, 0.65, 0.55));
  float3 H = normalize(L+V);
  float catchl = pow(saturate(dot(N,H)), 220.0);
  float2 c = p - float2(-0.22, 0.28);
  float blob = saturate(1.0 - length(c)/0.12);
  float3 color = iris + float3(1,1,1) * (catchl * 0.55 + blob * blob * 0.85);
  color = lerp(color, color * float3(1.08, 0.95, 0.75), 0.15);
  color = pow(saturate(color), 1.0/2.2);
  return float4(color, 1);
}";

    public const string PixelCornea = @"
cbuffer Frame : register(b0) {
  float4x4 viewProj;
  float4x4 model;
  float3 camPos; float time;
  float glow; float blink; float2 pad;
};
struct PSIn {
  float4 pos:SV_POSITION;
  float3 world:TEXCOORD0;
  float3 nrm:TEXCOORD1;
  float2 uv:TEXCOORD2;
  float3 local:TEXCOORD3;
};
float4 main(PSIn i) : SV_TARGET {
  float3 N = normalize(i.nrm);
  float3 V = normalize(camPos - i.world);
  float facing = saturate(dot(N, V));
  clip(facing - 0.15);
  float3 L = normalize(float3(0.4, 0.7, 0.5));
  float3 H = normalize(L+V);
  float fres = pow(1.0 - facing, 4.0);
  float spec = pow(saturate(dot(N,H)), 400.0);
  float3 color = float3(0.95, 0.97, 1.0) * (spec * 1.1 + fres * 0.12);
  float a = saturate(spec * 0.95 + fres * 0.18);
  clip(a - 0.02);
  return float4(color, a);
}";

    public const string PixelLid = @"
cbuffer Frame : register(b0) {
  float4x4 viewProj;
  float4x4 model;
  float3 camPos; float time;
  float glow; float blink; float2 pad;
};
struct PSIn {
  float4 pos:SV_POSITION;
  float3 world:TEXCOORD0;
  float3 nrm:TEXCOORD1;
  float2 uv:TEXCOORD2;
  float3 local:TEXCOORD3;
};
float4 main(PSIn i) : SV_TARGET {
  float3 skin = float3(0.72, 0.48, 0.36);
  float3 N = normalize(i.nrm);
  float3 L = normalize(float3(0.3,0.5,0.7));
  float ndl = saturate(dot(N,L)*0.5+0.5);
  float3 color = skin * (0.4 + ndl * 0.6);
  color = pow(color, 1.0/2.2);
  return float4(color, 0.95);
}";

    public const string PixelRing = @"
cbuffer Frame : register(b0) {
  float4x4 viewProj;
  float4x4 model;
  float3 camPos; float time;
  float glow; float blink; float2 pad;
};
cbuffer Ring : register(b1) {
  float3 color; float opacity;
};
struct PSIn {
  float4 pos:SV_POSITION;
  float3 world:TEXCOORD0;
  float3 nrm:TEXCOORD1;
  float2 uv:TEXCOORD2;
  float3 local:TEXCOORD3;
};
float4 main(PSIn i) : SV_TARGET {
  float3 N = normalize(i.nrm);
  float3 V = normalize(camPos - i.world);
  float fres = pow(1.0 - saturate(dot(N,V)), 2.6);
  float pulse = 0.92 + 0.08 * sin(time * 1.4 + i.local.x * 3.0);
  // Soft amber — dim the camera-facing arc so it doesn't chord across the face
  float3 headC = float3(0, 0.04, 0);
  float towardCam = dot(normalize(i.world - headC), normalize(camPos - headC));
  float frontDim = lerp(1.0, 0.12, pow(saturate(towardCam), 1.8));
  float3 c = color * (0.35 + fres * 0.40) * pulse;
  float a = saturate(opacity * (0.35 + fres * 0.30) * frontDim);
  clip(a - 0.02);
  return float4(c, a);
}";

    /// <summary>Fullscreen triangle VS — POSITION.xy = clip, zw unused; TEXCOORD = uv.</summary>
    public const string VertexFxaa = @"
struct VSIn { float2 pos:POSITION; float2 uv:TEXCOORD0; };
struct VSOut { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; };
VSOut main(VSIn i) {
  VSOut o;
  o.pos = float4(i.pos, 0, 1);
  o.uv = i.uv;
  return o;
}";

    /// <summary>Lightweight FXAA 3.11-ish quality preset.</summary>
    public const string PixelFxaa = @"
Texture2D sceneTex : register(t0);
SamplerState samp : register(s0);
cbuffer FxaaCB : register(b0) {
  float2 invSize;
  float2 _pad;
};
struct PSIn { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; };
float4 main(PSIn i) : SV_TARGET {
  float3 rgbM = sceneTex.SampleLevel(samp, i.uv, 0).rgb;
  float3 rgbNW = sceneTex.SampleLevel(samp, i.uv + float2(-1,-1)*invSize, 0).rgb;
  float3 rgbNE = sceneTex.SampleLevel(samp, i.uv + float2( 1,-1)*invSize, 0).rgb;
  float3 rgbSW = sceneTex.SampleLevel(samp, i.uv + float2(-1, 1)*invSize, 0).rgb;
  float3 rgbSE = sceneTex.SampleLevel(samp, i.uv + float2( 1, 1)*invSize, 0).rgb;
  float3 lumaW = float3(0.299, 0.587, 0.114);
  float lM = dot(rgbM, lumaW);
  float lNW = dot(rgbNW, lumaW);
  float lNE = dot(rgbNE, lumaW);
  float lSW = dot(rgbSW, lumaW);
  float lSE = dot(rgbSE, lumaW);
  float lMin = min(lM, min(min(lNW,lNE), min(lSW,lSE)));
  float lMax = max(lM, max(max(lNW,lNE), max(lSW,lSE)));
  float2 dir;
  dir.x = -((lNW + lNE) - (lSW + lSE));
  dir.y =  ((lNW + lSW) - (lNE + lSE));
  float dirReduce = max((lNW + lNE + lSW + lSE) * 0.03125, 0.0078125);
  float rcpDir = 1.0 / (min(abs(dir.x), abs(dir.y)) + dirReduce);
  dir = clamp(dir * rcpDir, float2(-8,-8), float2(8,8)) * invSize;
  float3 rgbA = 0.5 * (
    sceneTex.SampleLevel(samp, i.uv + dir * (1.0/3.0 - 0.5), 0).rgb +
    sceneTex.SampleLevel(samp, i.uv + dir * (2.0/3.0 - 0.5), 0).rgb);
  float3 rgbB = rgbA * 0.5 + 0.25 * (
    sceneTex.SampleLevel(samp, i.uv + dir * -0.5, 0).rgb +
    sceneTex.SampleLevel(samp, i.uv + dir *  0.5, 0).rgb);
  float lB = dot(rgbB, lumaW);
  if (lB < lMin || lB > lMax) return float4(rgbA, 1);
  return float4(rgbB, 1);
}";
}
