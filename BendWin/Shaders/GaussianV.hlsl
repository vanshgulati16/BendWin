// Vertical pass of separable Gaussian blur.
// Offsets scale with Sigma so blur radius adapts at every level.

cbuffer BlurParams : register(b0)
{
    float2 TexelSize;
    float  Sigma;
    float  _pad;
};

Texture2D    Source  : register(t0);
SamplerState Sampler : register(s0);

struct VSOutput { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

static const int   TAPS       = 13;
static const float OFFSETS[13] = { -3.0, -2.5, -2.0, -1.5, -1.0, -0.5, 0.0, 0.5, 1.0, 1.5, 2.0, 2.5, 3.0 };

float4 main(VSOutput input) : SV_TARGET
{
    float4 color = float4(0, 0, 0, 0);
    float  total = 0;
    float  sig   = max(Sigma, 0.1f);

    [unroll]
    for (int i = 0; i < TAPS; ++i)
    {
        float k = OFFSETS[i];
        float w = exp(-0.5f * k * k);
        float2 offset = float2(0, k * sig * TexelSize.y);
        color += Source.Sample(Sampler, input.uv + offset) * w;
        total += w;
    }

    return color / total;
}
