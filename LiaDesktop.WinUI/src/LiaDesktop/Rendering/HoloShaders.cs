namespace LiaDesktop.Rendering;

public static class HoloShaders
{
    public const string Vertex = """
cbuffer Frame : register(b0) {
  float4x4 viewProj;
  float4x4 model;
  float3 camPos;
  float time;
  float glow;
  float blink;
  float2 _pad;
};
struct VSIn {
  float3 pos : POSITION;
  float3 nrm : NORMAL;
  float4 col : COLOR;
};
struct VSOut {
  float4 pos : SV_POSITION;
  float3 world : TEXCOORD0;
  float3 nrm : TEXCOORD1;
  float4 col : COLOR;
  float3 local : TEXCOORD2;
};
VSOut main(VSIn i) {
  VSOut o;
  // Row-vector convention: model is Transpose(world)
  float4 wp = mul(float4(i.pos, 1), model);
  o.world = wp.xyz;
  o.local = i.pos;
  o.nrm = normalize(mul(i.nrm, (float3x3)model));
  o.col = i.col;
  o.pos = mul(wp, viewProj);
  return o;
}
""";

    /// <summary>Amber F.R.I.D.A.Y.-style hologram: dark interior, fresnel rim, Lambert/Blinn, scanlines, features.</summary>
    public const string PixelHead = """
cbuffer Frame : register(b0) {
  float4x4 viewProj;
  float4x4 model;
  float3 camPos;
  float time;
  float glow;
  float blink;
  float2 _pad;
};
struct PSIn {
  float4 pos : SV_POSITION;
  float3 world : TEXCOORD0;
  float3 nrm : TEXCOORD1;
  float4 col : COLOR;
  float3 local : TEXCOORD2;
};
float4 main(PSIn i) : SV_TARGET {
  float3 N = normalize(i.nrm);
  float3 V = normalize(camPos - i.world);
  float ndv = saturate(dot(N, V));

  // Key + fill lights (world-ish)
  float3 L1 = normalize(float3(0.35, 0.55, 0.75));
  float3 L2 = normalize(float3(-0.55, 0.15, 0.45));
  float diff = saturate(dot(N, L1)) * 0.55 + saturate(dot(N, L2)) * 0.28;
  float3 H = normalize(L1 + V);
  float spec = pow(saturate(dot(N, H)), 48.0) * 0.35;

  float fres = pow(1.0 - ndv, 2.2);
  float rim = pow(1.0 - ndv, 3.8);

  // Fine horizontal scanlines (not coarse mannequin bars)
  float scanWave = 0.5 + 0.5 * sin((i.local.y * 220.0 + time * 1.2) * 3.14159265);
  float scan = pow(scanWave, 9.0) * 0.11;
  float scanFine = pow(0.5 + 0.5 * sin((i.local.y * 480.0 - time * 0.85) * 3.14159265), 14.0) * 0.04;

  float flick = (0.994 + 0.006 * sin(time * 14.0 + i.local.y * 5.0)) * max(glow, 0.82);

  // Amber / gold palette (HUD match) — NOT neon cyan fill
  float3 deep = float3(0.02, 0.01, 0.008);          // near-black interior
  float3 amber = float3(1.0, 0.62, 0.22);            // #FF9E38-ish
  float3 gold = float3(1.0, 0.78, 0.42);
  float3 softRim = float3(1.0, 0.72, 0.38);

  // Mostly dark shell + fresnel rim + soft Lambert
  float shell = saturate(0.08 + fres * 0.72 + rim * 0.35 + diff * 0.22);
  float3 base = lerp(deep, amber, shell) * flick;
  base += amber * (scan + scanFine) * (0.55 + fres * 0.45);
  base += softRim * rim * 0.55;
  base += gold * spec;
  base += amber * diff * 0.18;

  // Vertex-color features + landmarks from lia_holo_mesh.bin meta
  float feat = saturate(i.col.g * 1.15 - fres * 0.35);
  // Lips around mouth_center y≈-0.098, z≈0.56
  float lipZone = smoothstep(-0.02, -0.06, i.local.y) * smoothstep(-0.22, -0.10, i.local.y)
                * smoothstep(0.22, 0.10, abs(i.local.x)) * smoothstep(0.35, 0.48, i.local.z);
  float browZone = smoothstep(0.30, 0.36, i.local.y) * smoothstep(0.44, 0.34, i.local.y)
                 * smoothstep(0.38, 0.16, abs(i.local.x));
  base += amber * feat * 0.85;
  base += gold * lipZone * (0.45 + feat * 0.7);
  base += amber * browZone * 0.28;

  // Nose bridge (subnasale ≈ -0.06 → brow)
  float nose = saturate(1.0 - abs(i.local.x) * 10.0)
             * smoothstep(-0.05, 0.08, i.local.y) * smoothstep(0.28, 0.12, i.local.y);
  nose *= saturate(i.local.z * 2.0);
  base += gold * nose * (0.22 + fres * 0.22);

  // Real eyes from mesh landmarks eye_L / eye_R (z≈0.39)
  float2 eyeL = float2(-0.161, 0.253);
  float2 eyeR = float2(0.152, 0.251);
  float dL = length((i.local.xy - eyeL) / float2(0.068, 0.032));
  float dR = length((i.local.xy - eyeR) / float2(0.068, 0.032));
  float dEye = min(dL, dR);
  float eyeMask = 1.0 - smoothstep(0.45, 1.05, dEye);
  eyeMask *= smoothstep(0.28, 0.38, i.local.z);
  float iris = 1.0 - smoothstep(0.08, 0.48, dEye);
  float pupil = 1.0 - smoothstep(0.0, 0.18, dEye);
  float open = 1.0 - blink;
  float3 sclera = float3(1.0, 0.92, 0.78);
  float3 irisCol = float3(0.42, 0.22, 0.06);
  // Bright sclera so eyes read as open (not dark sockets)
  base = lerp(base, sclera * 0.85 + amber * 0.08, eyeMask * 0.92 * open);
  base += irisCol * iris * open * 1.1;
  base += float3(0.02, 0.015, 0.01) * pupil * open * 1.4;
  base += gold * eyeMask * open * 0.18;
  // Spec highlight on iris
  base += float3(1.0, 0.95, 0.85) * pow(saturate(1.0 - length((i.local.xy - (dL < dR ? eyeL : eyeR)) / float2(0.02, 0.02) - float2(0.25, -0.2))), 8.0) * open * iris;
  // Blink closes the slit
  base = lerp(base, amber * 0.08, eyeMask * blink);

  // Smooth neck fade — no high-frequency scan on the cut (avoids speckles)
  float neck = smoothstep(-0.92, -0.52, i.local.y);
  float neckScanCut = lerp(1.0, 0.15, 1.0 - smoothstep(-0.7, -0.45, i.local.y));
  base = lerp(base * neck, base * neck * (1.0 - (scan + scanFine) * 0.85), 1.0 - neckScanCut);

  // Soft additive glow toward rim
  base += amber * fres * 0.12 * glow;

  return float4(saturate(base), 1.0);
}
""";

    public const string PixelRing = """
cbuffer Frame : register(b0) {
  float4x4 viewProj;
  float4x4 model;
  float3 camPos;
  float time;
  float glow;
  float blink;
  float2 _pad;
};
cbuffer Ring : register(b1) {
  float3 ringColor;
  float ringOpacity;
};
struct PSIn {
  float4 pos : SV_POSITION;
  float3 world : TEXCOORD0;
  float3 nrm : TEXCOORD1;
  float4 col : COLOR;
  float3 local : TEXCOORD2;
};
float4 main(PSIn i) : SV_TARGET {
  float3 N = normalize(i.nrm);
  float3 V = normalize(camPos - i.world);
  float ndv = saturate(dot(N, V));
  float fres = pow(1.0 - ndv, 1.8);
  float3 c = ringColor * (0.35 + fres * 0.95) * glow;
  // Thin bright rim
  c += ringColor * pow(fres, 2.5) * 0.4;
  return float4(c, ringOpacity * (0.4 + fres * 0.55));
}
""";
}
