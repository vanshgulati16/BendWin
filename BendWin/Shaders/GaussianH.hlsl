// Horizontal pass of a separable Gaussian blur.
// Kernel radius is controlled by the BlurRadius constant buffer.

cbuffer BlurParams : register(b0)
{
    float2 TexelSize; // (1/width, 1/height)
    float  Sigma;
    float  _pad;
};

Texture2D    Source  : register(t0);
SamplerState Sampler : register(s0);

struct VSOutput { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

// Precomputed 9-tap Gaussian weights for the current sigma (approximation)
static const int   TAPS     = 9;
static const float OFFSETS[9] = { -4, -3, -2, -1, 0, 1, 2, 3, 4 };

float GaussianWeight(float x, float sigma)
{
    return exp(-0.5f * (x * x) / (sigma * sigma));
}

float4 main(VSOutput input) : SV_TARGET
{
    float4 color  = float4(0, 0, 0, 0);
    float  total  = 0;

    [unroll]
    for (int i = 0; i < TAPS; ++i)
    {
        float w = GaussianWeight(OFFSETS[i], max(Sigma, 0.001f));
        float2 offset = float2(OFFSETS[i] * TexelSize.x, 0);
        color += Source.Sample(Sampler, input.uv + offset) * w;
        total += w;
    }

    return color / total;
}
