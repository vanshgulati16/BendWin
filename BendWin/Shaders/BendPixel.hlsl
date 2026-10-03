// Page-fold effect — port of BendMac's Bend.metal.
//
// Uses an inverse homography to simulate the screen folding around a bottom hinge.
// Lower edge stays anchored; upper corners taper inward proportionally to progress.
// Progressive blur is applied by blending 5 pre-blurred textures weighted by height.

cbuffer BendParams : register(b0)
{
    float Progress;    // 0 = flat, 1 = fully closed
    float Perspective; // 0-1: how far upper edges taper inward
    float Blur;        // 0-1: overall blur intensity
    float Shadow;      // 0-1: edge shadow depth
    float Style;       // 0=Silk, 1=Shade, 2=Frost
    float3 _pad;
};

Texture2D    T0 : register(t0); // original frame
Texture2D    T1 : register(t1); // blur level 1 (sigma ~1)
Texture2D    T2 : register(t2); // blur level 2 (sigma ~2)
Texture2D    T3 : register(t3); // blur level 3 (sigma ~4)
Texture2D    T4 : register(t4); // blur level 4 (sigma ~8)
Texture2D    T5 : register(t5); // blur level 5 (sigma ~16)

SamplerState S0 : register(s0);

struct PSInput { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

// Inverse projective (homography) mapping for the fold.
// Returns source UV to sample, or (-1,-1) if outside the folded region.
float2 FoldUV(float2 uv)
{
    float p    = Progress;
    float persp = Perspective * 0.45f;

    // Height factor: 0 at hinge (bottom), 1 at top
    float h = 1.0f - uv.y;

    // Taper: upper corners move inward as fold progresses
    float inset = p * persp * h;

    // Clip pixels outside the fold silhouette
    if (uv.x < inset || uv.x > 1.0f - inset)
        return float2(-1.0f, -1.0f);

    // Map from tapered space back to original [0,1] x [0,1]
    float src_x = (uv.x - inset) / max(1.0f - 2.0f * inset, 0.0001f);
    float src_y = uv.y;

    return float2(src_x, src_y);
}

float4 SampleBlurred(float2 src, float weight)
{
    // weight in [0,5] selects a tier of blur
    float4 c0 = T0.Sample(S0, src);
    float4 c1 = T1.Sample(S0, src);
    float4 c2 = T2.Sample(S0, src);
    float4 c3 = T3.Sample(S0, src);
    float4 c4 = T4.Sample(S0, src);
    float4 c5 = T5.Sample(S0, src);

    float w = saturate(weight);
    if (w < 0.2f) return lerp(c0, c1, w / 0.2f);
    if (w < 0.4f) return lerp(c1, c2, (w - 0.2f) / 0.2f);
    if (w < 0.6f) return lerp(c2, c3, (w - 0.4f) / 0.2f);
    if (w < 0.8f) return lerp(c3, c4, (w - 0.6f) / 0.2f);
    return         lerp(c4, c5, (w - 0.8f) / 0.2f);
}

float4 main(PSInput input) : SV_TARGET
{
    if (Progress < 0.005f)
        return float4(0, 0, 0, 0); // completely transparent when not folding

    float2 uv = input.uv;
    float2 src = FoldUV(uv);

    if (src.x < 0.0f)
        return float4(0, 0, 0, 0); // outside fold silhouette

    // Height along the folded sheet (0=hinge, 1=top edge)
    float h = 1.0f - uv.y;

    // Blur weight: increases toward the top and with progress
    float blurWeight = h * Progress * Blur;

    float4 color = SampleBlurred(src, blurWeight);

    // ── Edge shadow ──────────────────────────────────────────────────────────
    float persp = Perspective * 0.45f;
    float inset = Progress * persp * h;
    float edgeDist = min(uv.x - inset, (1.0f - inset) - uv.x);
    float edgeFade = 1.0f - exp(-edgeDist / max(inset + 0.005f, 0.001f) * 3.0f);
    float edgeDark = (1.0f - edgeFade) * Shadow * Progress;
    color.rgb *= 1.0f - edgeDark;

    // ── Top-edge feathering ──────────────────────────────────────────────────
    float topFeather = smoothstep(0.0f, 0.025f, uv.y);
    color.rgb *= topFeather;

    // ── Style overlay (Frost = 2, Shade = 1) ────────────────────────────────
    if (Style > 1.5f)
    {
        // Frost: cool blue-white tint
        float tint = 0.10f * Progress * h;
        color.rgb = lerp(color.rgb, float3(0.88f, 0.92f, 1.00f), tint);
    }
    else if (Style > 0.5f)
    {
        // Shade: warm dark vignette
        float shade = 0.15f * Progress * h;
        color.rgb *= 1.0f - shade;
    }

    color.a = 1.0f;
    return color;
}
