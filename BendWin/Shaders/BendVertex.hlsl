// Full-screen triangle — no vertex buffer needed.
// Three vertices cover the entire clip space using SV_VertexID.

struct VSOutput
{
    float4 pos : SV_POSITION;
    float2 uv  : TEXCOORD0;
};

VSOutput main(uint id : SV_VertexID)
{
    // Positions for a clockwise full-screen triangle
    float2 pos = float2(
        (id == 2) ? 3.0f : -1.0f,
        (id == 1) ? -3.0f :  1.0f
    );
    VSOutput o;
    o.pos = float4(pos, 0.0f, 1.0f);
    // UV (0,0) = top-left, (1,1) = bottom-right
    o.uv = float2(pos.x * 0.5f + 0.5f, -pos.y * 0.5f + 0.5f);
    return o;
}
