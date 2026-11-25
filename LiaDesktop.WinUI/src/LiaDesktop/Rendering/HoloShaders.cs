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
  float4 wp = mul(float4(i.pos, 1), model);
  o.world = wp.xyz;
  o.local = i.pos;
  o.nrm = normalize(mul(i.nrm, (float3x3)model));
  o.col = i.col;
  o.pos = mul(wp, viewProj);
  return o;
}
""";

    /// <summary>Amber hologram head. Eye sockets are discarded so 3D eyeballs show through.</summary>
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
  float2 eyeL = float2(-0.161, 0.253);
  float2 eyeR = float2(0.152, 0.251);
  // Almond fissure sized to lids + sphere (depth-tested fill, no void)
  float dL = length((i.local.xy - eyeL) / float2(0.044, 0.022));
  float dR = length((i.local.xy - eyeR) / float2(0.044, 0.022));
  float dEye = min(dL, dR);
  if (dEye < 0.98 && i.local.z > 0.32) discard;

  float3 N = normalize(i.nrm);
  float3 V = normalize(camPos - i.world);
  float ndv = saturate(dot(N, V));

  float3 L1 = normalize(float3(0.35, 0.55, 0.75));
  float3 L2 = normalize(float3(-0.55, 0.15, 0.45));
  float diff = saturate(dot(N, L1)) * 0.55 + saturate(dot(N, L2)) * 0.28;
  float3 H = normalize(L1 + V);
  float spec = pow(saturate(dot(N, H)), 48.0) * 0.35;

  float fres = pow(1.0 - ndv, 2.2);
  float rim = pow(1.0 - ndv, 3.8);

  float scanWave = 0.5 + 0.5 * sin((i.local.y * 220.0 + time * 1.2) * 3.14159265);
  float scan = pow(scanWave, 9.0) * 0.11;
  float scanFine = pow(0.5 + 0.5 * sin((i.local.y * 480.0 - time * 0.85) * 3.14159265), 14.0) * 0.04;
  float flick = (0.994 + 0.006 * sin(time * 14.0 + i.local.y * 5.0)) * max(glow, 0.82);

  float3 deep = float3(0.02, 0.01, 0.008);
  float3 amber = float3(1.0, 0.62, 0.22);
  float3 gold = float3(1.0, 0.78, 0.42);
  float3 softRim = float3(1.0, 0.72, 0.38);

  float shell = saturate(0.08 + fres * 0.72 + rim * 0.35 + diff * 0.22);
  float3 base = lerp(deep, amber, shell) * flick;
  base += amber * (scan + scanFine) * (0.55 + fres * 0.45);
  base += softRim * rim * 0.55;
  base += gold * spec;
  base += amber * diff * 0.18;

  float feat = saturate(i.col.g * 1.15 - fres * 0.35);
  float lipZone = smoothstep(-0.02, -0.06, i.local.y) * smoothstep(-0.22, -0.10, i.local.y)
                * smoothstep(0.22, 0.10, abs(i.local.x)) * smoothstep(0.35, 0.48, i.local.z);
  // Brows: ridge above sockets, readable via Lambert + gold lift
  float browZone = smoothstep(0.30, 0.355, i.local.y) * smoothstep(0.46, 0.36, i.local.y)
                 * smoothstep(0.40, 0.14, abs(i.local.x)) * smoothstep(0.25, 0.38, i.local.z);
  base += amber * feat * 0.85;
  base += gold * lipZone * (0.45 + feat * 0.7);
  base += gold * browZone * (0.55 + diff * 0.4);
  base += amber * browZone * fres * 0.35;

  float nose = saturate(1.0 - abs(i.local.x) * 10.0)
             * smoothstep(-0.05, 0.08, i.local.y) * smoothstep(0.28, 0.12, i.local.y);
  nose *= saturate(i.local.z * 2.0);
  base += gold * nose * (0.22 + fres * 0.22);

  float neck = smoothstep(-0.92, -0.52, i.local.y);
  float neckScanCut = lerp(1.0, 0.15, 1.0 - smoothstep(-0.7, -0.45, i.local.y));
  base = lerp(base * neck, base * neck * (1.0 - (scan + scanFine) * 0.85), 1.0 - neckScanCut);
  base += amber * fres * 0.12 * glow;

  return float4(saturate(base), 1.0);
}
""";

    /// <summary>Eyeball sphere sclera — hologram amber tint, dimmer than skin, soft corner falloff.</summary>
    public const string PixelEye = """
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
  float3 L = normalize(float3(0.35, 0.55, 0.75));
  float diff = saturate(dot(N, L)) * 0.45 + 0.2;
  float fres = pow(1.0 - ndv, 2.4);

  float3 amber = float3(1.0, 0.62, 0.22);
  float3 gold = float3(1.0, 0.78, 0.42);
  // Amber-tinted sclera — dimmer than skin, never cream-white, never black void
  float3 sclera = float3(0.72, 0.52, 0.30);
  float front = saturate(i.local.z * 1.4 + 0.15);
  // Soft falloff toward sphere limbs (corners of the visible eye)
  float limb = saturate(1.0 - length(i.local.xy) * 1.05);
  float3 c = sclera * (0.55 + diff * 0.45) * (0.62 + limb * 0.45);
  c += amber * fres * 0.28 * glow;
  c += gold * front * 0.10;
  c *= 0.94 + 0.06 * sin(i.local.y * 40.0); // faint holo grain
  return float4(saturate(c), 1.0);
}
""";

    /// <summary>Iris disc on the sphere (+Z). Slightly concave via normals; pupil + specular.</summary>
    public const string PixelIris = """
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
  float3 L = normalize(float3(0.4, 0.55, 0.7));
  float3 H = normalize(L + V);
  float spec = pow(saturate(dot(N, H)), 72.0);

  float2 xy = i.local.xy;
  float r = length(xy);
  float irisR = 0.98;
  float pupilR = 0.26;
  float irisMask = 1.0 - smoothstep(irisR * 0.88, irisR, r);
  float pupilMask = 1.0 - smoothstep(pupilR * 0.55, pupilR, r);
  float limbus = smoothstep(irisR * 0.72, irisR, r) * (1.0 - smoothstep(irisR, irisR * 1.05, r));

  float ang = atan2(xy.y, xy.x);
  float spoke = 0.78 + 0.22 * sin(ang * 11.0 + r * 14.0);
  float3 amber = float3(1.0, 0.62, 0.22);
  float3 irisOuter = float3(0.42, 0.18, 0.05);
  float3 irisInner = float3(0.95, 0.55, 0.16);
  float3 pupil = float3(0.04, 0.025, 0.015);
  float3 iris = lerp(irisInner, irisOuter, saturate(r / irisR)) * spoke;

  float3 col = iris * irisMask;
  col = lerp(col, pupil, pupilMask);
  col += amber * limbus * 0.55;
  col += float3(1.0, 0.96, 0.88) * spec * (0.75 + irisMask * 0.85);
  // Single catchlight
  float2 catchUv = xy - float2(0.18, 0.16);
  col += float3(1.0, 0.97, 0.90) * (1.0 - smoothstep(0.0, 0.10, length(catchUv))) * irisMask * 1.1;
  col *= 0.92 + 0.12 * ndv;
  return float4(saturate(col), 1.0);
}
""";

    /// <summary>Eyelid shell — matches head hologram skin.</summary>
    public const string PixelLid = """
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
  float3 L = normalize(float3(0.35, 0.55, 0.75));
  float diff = saturate(dot(N, L)) * 0.5;
  float fres = pow(1.0 - ndv, 2.4);
  float3 amber = float3(1.0, 0.62, 0.22);
  float3 gold = float3(1.0, 0.78, 0.42);
  float3 deep = float3(0.06, 0.03, 0.015);
  float3 c = lerp(deep, amber, 0.22 + fres * 0.65 + diff * 0.28) * glow;
  c += gold * fres * 0.25;
  float scan = pow(0.5 + 0.5 * sin(i.local.y * 180.0 + time), 10.0) * 0.08;
  c += amber * scan;
  return float4(saturate(c), 1.0);
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
  // Dim the arc behind the head (camera looks from +Z; world.z < 0 is back)
  float backDim = lerp(1.0, 0.18, saturate((-i.world.z - 0.02) * 4.0));
  float3 c = ringColor * (0.35 + fres * 0.95) * glow * backDim;
  c += ringColor * pow(fres, 2.5) * 0.35 * backDim;
  float a = ringOpacity * (0.35 + fres * 0.55) * backDim;
  return float4(c, a);
}
""";
}
