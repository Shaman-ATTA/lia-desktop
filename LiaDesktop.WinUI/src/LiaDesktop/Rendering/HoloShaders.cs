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
  // Almond fissure sized to smaller natural eyes
  float dL = length((i.local.xy - eyeL) / float2(0.040, 0.020));
  float dR = length((i.local.xy - eyeR) / float2(0.040, 0.020));
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

  float feat = saturate(i.col.g * 0.55 - fres * 0.25);
  // Lips — same amber family, slightly darker/saturated; cupid's bow, NO bloom
  float lipY = i.local.y + 0.06;
  float lipZone = smoothstep(0.035, -0.005, abs(lipY) - 0.01)
                * smoothstep(0.14, 0.05, abs(i.local.x))
                * smoothstep(0.32, 0.42, i.local.z);
  // Upper/lower split + cupid's bow dip at center
  float upperLip = saturate(1.0 - abs(lipY - 0.012) / 0.018) * lipZone;
  float lowerLip = saturate(1.0 - abs(lipY + 0.016) / 0.022) * lipZone;
  float cupid = saturate(1.0 - abs(i.local.x) / 0.025) * upperLip;
  float lipLine = saturate(1.0 - abs(lipY) / 0.006) * lipZone; // seam between lips
  float3 lipTint = float3(0.78, 0.38, 0.22); // darker saturated amber
  base = lerp(base, base * float3(0.92, 0.72, 0.62) + lipTint * 0.22, saturate(upperLip + lowerLip) * 0.85);
  base *= 1.0 - lipLine * 0.18; // visible lip seam
  base *= 1.0 - cupid * 0.08;   // cupid's bow shadow
  // Soft mouth cavity — dark amber, never emissive white/teeth bloom
  float cavity = smoothstep(0.38, 0.48, i.local.z) * smoothstep(-0.02, -0.08, i.local.y)
               * smoothstep(-0.14, -0.05, i.local.y) * smoothstep(0.08, 0.02, abs(i.local.x));
  base = lerp(base, deep * 1.5, cavity * 0.45 * (1.0 - lipZone));

  float browZone = smoothstep(0.30, 0.355, i.local.y) * smoothstep(0.42, 0.36, i.local.y)
                 * smoothstep(0.34, 0.12, abs(i.local.x)) * smoothstep(0.22, 0.36, i.local.z);
  float cheek = smoothstep(0.05, 0.14, abs(i.local.x)) * smoothstep(0.28, 0.18, abs(i.local.x))
              * smoothstep(0.02, 0.10, i.local.y) * smoothstep(0.22, 0.08, i.local.y)
              * smoothstep(0.20, 0.35, i.local.z);
  base += amber * feat * 0.22;
  base += amber * browZone * 0.12 * diff;
  base += blush * cheek * 0.12;

  float nose = saturate(1.0 - abs(i.local.x) * 12.0)
             * smoothstep(-0.02, 0.10, i.local.y) * smoothstep(0.26, 0.10, i.local.y);
  nose *= saturate(i.local.z * 2.2);
  base += gold * nose * (0.10 + fres * 0.12);

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
  float scan = pow(0.5 + 0.5 * sin(i.local.y * 520.0 + time * 1.4), 14.0) * 0.10;
  // Finer strand detail
  float strand = 0.78 + 0.12 * sin(i.local.x * 160.0 + i.local.z * 70.0)
               + 0.10 * sin(i.local.x * 340.0 - i.local.y * 90.0);
  float3 c = lerp(amber * 0.35, gold, fres * 0.65 + diff * 0.28) * strand * glow;
  c += amber * scan * 0.85;
  float a = saturate(0.20 + fres * 0.42 + scan * 0.15) * 0.70;
  return float4(c, a);
}
""";

    /// <summary>Sclera — soft pale amber blended with skin family (no grey/silver).</summary>
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
  float wrap = saturate((dot(N, L) + 0.3) / 1.3);
  float fres = pow(1.0 - ndv, 2.4);

  float3 amber = float3(1.0, 0.62, 0.22);
  // Soft pale amber — skin-adjacent, never grey metal
  float3 sclera = float3(0.72, 0.52, 0.32);
  float limb = saturate(1.0 - length(i.local.xy) * 1.05);
  float3 c = sclera * (0.55 + wrap * 0.35) * (0.65 + limb * 0.4);
  c += amber * fres * 0.18 * glow;
  c = lerp(c, amber * 0.45, (1.0 - limb) * 0.25); // soft corner blend to skin tone
  return float4(saturate(c), 1.0);
}
""";

    /// <summary>Iris — amber glow, dark pupil, one small catchlight (not metallic).</summary>
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
  float spec = pow(saturate(dot(N, H)), 96.0);

  float2 xy = i.local.xy;
  float r = length(xy);
  float irisR = 0.98;
  float pupilR = 0.30;
  float irisMask = 1.0 - smoothstep(irisR * 0.88, irisR, r);
  float pupilMask = 1.0 - smoothstep(pupilR * 0.55, pupilR, r);
  float limbus = smoothstep(irisR * 0.72, irisR, r) * (1.0 - smoothstep(irisR, irisR * 1.05, r));

  float ang = atan2(xy.y, xy.x);
  float spoke = 0.80 + 0.20 * sin(ang * 10.0 + r * 12.0);
  float3 amber = float3(1.0, 0.62, 0.22);
  float3 irisOuter = float3(0.55, 0.28, 0.08);
  float3 irisInner = float3(0.95, 0.55, 0.16);
  float3 pupil = float3(0.02, 0.012, 0.008);
  float3 iris = lerp(irisInner, irisOuter, saturate(r / irisR)) * spoke;

  float3 col = iris * irisMask;
  col = lerp(col, pupil, pupilMask);
  col += amber * limbus * 0.45;
  col += amber * irisMask * 0.22 * glow; // soft iris glow
  // One small catchlight only — not chrome ball
  float2 catchUv = xy - float2(0.18, 0.16);
  float catchlight = (1.0 - smoothstep(0.0, 0.08, length(catchUv))) * irisMask;
  col += float3(1.0, 0.95, 0.85) * catchlight * 0.9;
  col += float3(1.0, 0.96, 0.88) * spec * 0.25 * irisMask;
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

    /// <summary>Lash line — thin dark-amber rim over lids.</summary>
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
  float3 deep = float3(0.06, 0.03, 0.012);
  float3 N = normalize(i.nrm);
  float3 V = normalize(camPos - i.world);
  float fres = pow(1.0 - saturate(dot(N, V)), 1.8);
  float3 c = lerp(deep, amber, 0.28 + fres * 0.4) * glow;
  return float4(saturate(c), 1.0);
}
""";

    /// <summary>Brows — thinner feminine arches, darker amber holo, lower opacity.</summary>
    public const string PixelBrow = """
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
  float fres = pow(1.0 - saturate(dot(N, V)), 2.0);
  float3 amber = float3(1.0, 0.55, 0.18);
  float3 deep = float3(0.12, 0.06, 0.025);
  float scan = pow(0.5 + 0.5 * sin(i.local.x * 200.0 + time), 10.0) * 0.06;
  float3 c = lerp(deep, amber, 0.4 + fres * 0.35) * glow;
  c += amber * scan;
  float a = saturate(0.35 + fres * 0.25) * 0.55;
  return float4(c, a);
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
