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
  float4 wp = mul(float4(i.pos,1), model);
  o.world = wp.xyz;
  o.local = i.pos;
  o.nrm = normalize(mul(i.nrm, (float3x3)model));
  o.col = i.col;
  o.pos = mul(wp, viewProj);
  return o;
}
""";

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
  float fres = pow(1.0 - ndv, 2.15);
  float rim = pow(1.0 - ndv, 3.6);
  float scanWave = 0.5 + 0.5 * sin((i.world.y * 280.0 + time * 1.35) * 3.14159265);
  float scan = pow(scanWave, 7.0) * 0.14;
  float scanFine = pow(0.5 + 0.5 * sin((i.world.y * 520.0 - time * 0.9) * 3.14159265), 12.0) * 0.05;
  float flick = (0.992 + 0.008 * sin(time * 18.0 + i.world.y * 7.0)) * max(glow, 0.78);
  float3 deep = float3(0.018, 0.045, 0.085);
  float3 cyan = float3(0.22, 0.92, 1.0);
  float3 soft = float3(0.62, 0.88, 1.0);
  float fill = saturate(0.16 + fres * 0.82 + rim * 0.3);
  float3 base = lerp(deep, cyan, fill) * flick;
  base += cyan * (scan + scanFine);
  base += soft * rim * 0.48;
  float feat = max(0.0, i.col.g - fres * 0.55);
  base += cyan * feat * 0.7;
  float2 eyeL = float2(-0.155, 0.252);
  float2 eyeR = float2(0.152, 0.251);
  float dL = length((i.local.xy - eyeL) / float2(0.055, 0.028));
  float dR = length((i.local.xy - eyeR) / float2(0.055, 0.028));
  float eyeMask = 1.0 - smoothstep(0.55, 1.15, min(dL, dR));
  eyeMask *= smoothstep(0.25, 0.45, i.local.z);
  float iris = 1.0 - smoothstep(0.15, 0.55, min(dL, dR));
  base += float3(0.15, 0.75, 0.95) * eyeMask * (0.35 + 0.65 * (1.0 - blink)) * 0.55;
  base += float3(0.4, 0.95, 1.0) * iris * (1.0 - blink) * 0.85;
  base *= lerp(1.0, 0.22, eyeMask * blink);
  float neck = smoothstep(-0.95, -0.58, i.world.y);
  base *= neck;
  return float4(base, 1.0);
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
  float fres = pow(1.0 - ndv, 2.0);
  float3 c = ringColor * (0.45 + fres * 0.85) * glow;
  return float4(c, ringOpacity * (0.55 + fres * 0.45));
}
""";
}
