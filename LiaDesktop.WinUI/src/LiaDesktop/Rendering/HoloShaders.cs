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

    /// <summary>Amber hologram female head — wrap lighting, fine scanlines, glitch shimmer.</summary>
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
  float2 eyeL = float2(-0.157, 0.250);
  float2 eyeR = float2( 0.157, 0.250);
  // Almond fissure for recessed spheres
  float dL = length((i.local.xy - eyeL) / float2(0.048, 0.024));
  float dR = length((i.local.xy - eyeR) / float2(0.048, 0.024));
  float dEye = min(dL, dR);
  if (dEye < 0.98 && i.local.z > 0.28) discard;

  float3 N = normalize(i.nrm);
  float3 V = normalize(camPos - i.world);
  float ndv = saturate(dot(N, V));

  float3 L1 = normalize(float3(0.35, 0.55, 0.75));
  float3 L2 = normalize(float3(-0.55, 0.20, 0.40));
  float3 L3 = normalize(float3(0.0, -0.4, 0.5)); // soft fill / wrap
  float wrap = saturate((dot(N, L1) + 0.35) / 1.35);
  float diff = wrap * 0.55 + saturate(dot(N, L2)) * 0.28 + saturate(dot(N, L3) + 0.2) * 0.18;
  float3 H = normalize(L1 + V);
  float spec = pow(saturate(dot(N, H)), 64.0) * 0.32;

  float fres = pow(1.0 - ndv, 2.0);
  float rim = pow(1.0 - ndv, 4.2);

  // Finer scanlines
  float scanWave = 0.5 + 0.5 * sin((i.local.y * 380.0 + time * 1.35) * 3.14159265);
  float scan = pow(scanWave, 12.0) * 0.09;
  float scanFine = pow(0.5 + 0.5 * sin((i.local.y * 820.0 - time * 1.1) * 3.14159265), 18.0) * 0.035;
  // Tasteful glitch shimmer
  float glitch = step(0.992, frac(sin(dot(i.local.xy + time * 0.07, float2(12.9898,78.233))) * 43758.5453));
  float flick = (0.992 + 0.008 * sin(time * 11.0 + i.local.y * 6.0)) * max(glow, 0.85);

  float3 deep = float3(0.018, 0.010, 0.008);
  float3 amber = float3(1.0, 0.62, 0.22);
  float3 gold = float3(1.0, 0.80, 0.45);
  float3 softRim = float3(1.0, 0.74, 0.40);
  float3 blush = float3(1.0, 0.45, 0.32);

  float shell = saturate(0.10 + fres * 0.68 + rim * 0.38 + diff * 0.24);
  float3 base = lerp(deep, amber, shell) * flick;
  base += amber * (scan + scanFine) * (0.5 + fres * 0.5);
  base += softRim * rim * 0.62;
  base += gold * spec;
  base += amber * diff * 0.20;
  base += amber * glitch * 0.55;

  float feat = saturate(i.col.g * 1.2 - fres * 0.3);
  // Lips — fuller feminine zone
  float lipZone = smoothstep(-0.02, -0.055, i.local.y) * smoothstep(-0.14, -0.06, i.local.y)
                * smoothstep(0.16, 0.06, abs(i.local.x)) * smoothstep(0.30, 0.42, i.local.z);
  float browZone = smoothstep(0.30, 0.355, i.local.y) * smoothstep(0.45, 0.36, i.local.y)
                 * smoothstep(0.36, 0.12, abs(i.local.x)) * smoothstep(0.22, 0.36, i.local.z);
  // Cheek blush
  float cheek = smoothstep(0.05, 0.14, abs(i.local.x)) * smoothstep(0.28, 0.18, abs(i.local.x))
              * smoothstep(0.02, 0.10, i.local.y) * smoothstep(0.22, 0.08, i.local.y)
              * smoothstep(0.20, 0.35, i.local.z);
  base += amber * feat * 0.75;
  base += gold * lipZone * (0.55 + feat * 0.85);
  base += blush * lipZone * 0.22;
  base += gold * browZone * (0.5 + diff * 0.35);
  base += blush * cheek * 0.18;

  float nose = saturate(1.0 - abs(i.local.x) * 12.0)
             * smoothstep(-0.02, 0.10, i.local.y) * smoothstep(0.26, 0.10, i.local.y);
  nose *= saturate(i.local.z * 2.2);
  base += gold * nose * (0.18 + fres * 0.2);

  float neck = smoothstep(-0.55, -0.25, i.local.y);
  float neckScanCut = lerp(1.0, 0.18, 1.0 - smoothstep(-0.45, -0.22, i.local.y));
  base = lerp(base * neck, base * neck * (1.0 - (scan + scanFine) * 0.8), 1.0 - neckScanCut);
  base += amber * fres * 0.14 * glow;

  return float4(saturate(base), 1.0);
}
""";

    /// <summary>Hair volume — same amber hologram, lower opacity + scanlines.</summary>
    public const string PixelHair = """
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
  float fres = pow(1.0 - ndv, 1.6);
  float3 L = normalize(float3(0.3, 0.7, 0.5));
  float diff = saturate(dot(N, L)) * 0.45 + 0.25;
  float3 amber = float3(1.0, 0.62, 0.22);
  float3 gold = float3(1.0, 0.78, 0.42);
  float scan = pow(0.5 + 0.5 * sin(i.local.y * 520.0 + time * 1.4), 14.0) * 0.12;
  float strand = 0.85 + 0.15 * sin(i.local.x * 90.0 + i.local.z * 40.0);
  float3 c = lerp(amber * 0.35, gold, fres * 0.7 + diff * 0.3) * strand * glow;
  c += amber * scan;
  float a = saturate(0.22 + fres * 0.45 + scan * 0.2) * 0.72;
  return float4(c, a);
}
""";

    /// <summary>Eyeball sphere — brighter amber sclera.</summary>
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
  float diff = saturate(dot(N, L)) * 0.5 + 0.28;
  float fres = pow(1.0 - ndv, 2.2);

  float3 amber = float3(1.0, 0.62, 0.22);
  float3 gold = float3(1.0, 0.82, 0.48);
  float3 sclera = float3(0.85, 0.68, 0.42);
  float front = saturate(i.local.z * 1.5 + 0.2);
  float limb = saturate(1.0 - length(i.local.xy) * 1.0);
  float3 c = sclera * (0.65 + diff * 0.5) * (0.7 + limb * 0.4);
  c += amber * fres * 0.32 * glow;
  c += gold * front * 0.14;
  return float4(saturate(c), 1.0);
}
""";

    /// <summary>Iris — larger readable glow + catchlight.</summary>
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
  float3 L = normalize(float3(0.4, 0.55, 0.7));
  float3 H = normalize(L + V);
  float spec = pow(saturate(dot(N, H)), 80.0);

  float2 xy = i.local.xy;
  float r = length(xy);
  float irisR = 0.98;
  float pupilR = 0.24;
  float irisMask = 1.0 - smoothstep(irisR * 0.86, irisR, r);
  float pupilMask = 1.0 - smoothstep(pupilR * 0.5, pupilR, r);
  float limbus = smoothstep(irisR * 0.7, irisR, r) * (1.0 - smoothstep(irisR, irisR * 1.06, r));

  float ang = atan2(xy.y, xy.x);
  float spoke = 0.75 + 0.25 * sin(ang * 12.0 + r * 16.0);
  float3 amber = float3(1.0, 0.62, 0.22);
  float3 irisOuter = float3(0.55, 0.28, 0.08);
  float3 irisInner = float3(1.0, 0.72, 0.28);
  float3 pupil = float3(0.03, 0.02, 0.015);
  float3 iris = lerp(irisInner, irisOuter, saturate(r / irisR)) * spoke;

  float3 col = iris * irisMask;
  col = lerp(col, pupil, pupilMask);
  col += amber * limbus * 0.7;
  col += amber * irisMask * 0.18 * glow; // iris glow
  col += float3(1.0, 0.97, 0.9) * spec * (0.9 + irisMask);
  float2 catchUv = xy - float2(0.2, 0.18);
  col += float3(1.0, 0.98, 0.92) * (1.0 - smoothstep(0.0, 0.095, length(catchUv))) * irisMask * 1.25;
  return float4(saturate(col), 1.0);
}
""";

    /// <summary>Eyelid / lash line — dark amber rim.</summary>
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
  float diff = saturate(dot(N, L)) * 0.45;
  float fres = pow(1.0 - ndv, 2.2);
  float3 amber = float3(1.0, 0.62, 0.22);
  float3 gold = float3(1.0, 0.78, 0.42);
  float3 deep = float3(0.05, 0.025, 0.012);
  float3 c = lerp(deep, amber, 0.28 + fres * 0.55 + diff * 0.25) * glow;
  c += gold * fres * 0.2;
  return float4(saturate(c), 1.0);
}
""";

    /// <summary>Lash strands — denser dark rim.</summary>
    public const string PixelLash = """
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
  float3 amber = float3(1.0, 0.55, 0.18);
  float3 deep = float3(0.08, 0.04, 0.015);
  float3 N = normalize(i.nrm);
  float3 V = normalize(camPos - i.world);
  float fres = pow(1.0 - saturate(dot(N, V)), 1.8);
  float3 c = lerp(deep, amber, 0.35 + fres * 0.5) * glow;
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
  float backDim = lerp(1.0, 0.18, saturate((-i.world.z - 0.02) * 4.0));
  float3 c = ringColor * (0.35 + fres * 0.95) * glow * backDim;
  c += ringColor * pow(fres, 2.5) * 0.35 * backDim;
  float a = ringOpacity * (0.35 + fres * 0.55) * backDim;
  return float4(c, a);
}
""";
}
