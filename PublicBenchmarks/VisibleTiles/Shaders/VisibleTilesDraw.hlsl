#ifndef SUMMIT_VISIBLE_TILES_DRAW_INCLUDED
#define SUMMIT_VISIBLE_TILES_DRAW_INCLUDED
StructuredBuffer<float3> _Positions;
StructuredBuffer<uint> _SourceIndices;
StructuredBuffer<uint> _VisibleOutput;
float4x4 _ClipFromWorld;
float _TileTriangles;
float _FlipWinding;

// Same 32-bit descriptor contract as Bfp2ResolveSourceVertex: two words,
// original-index offset and valid count. No copied visible-index list in tile mode.
uint ResolveSourceVertex(uint id)
{
    uint tileTriangles = (uint)_TileTriangles;
    if (tileTriangles == 0u) return _VisibleOutput[id];
    uint tileSize = tileTriangles * 3u;
    uint tile = id / tileSize;
    uint localIndex = id - tile * tileSize;
    uint first = _VisibleOutput[tile * 2u];
    uint count = _VisibleOutput[tile * 2u + 1u];
    // Pad incomplete tiles with degenerate triangles, not a source OOB read.
    uint offset = localIndex < count ? localIndex : 0u;
    if (localIndex < count && _FlipWinding > 0.5)
    {
        uint lane = offset % 3u;
        if (lane == 1u) offset++;
        else if (lane == 2u) offset--;
    }
    return _SourceIndices[first + offset];
}
struct Varyings { float4 position : SV_POSITION; float4 color : COLOR0; };
Varyings Vert(uint id : SV_VertexID)
{
    uint source = ResolveSourceVertex(id);
    uint triangleId = source / 3u;
    Varyings o;
    o.position = mul(_ClipFromWorld, float4(_Positions[source], 1));
    o.color = float4(float3((triangleId*17u)%251u+4u,
        (triangleId*47u)%251u+4u, (triangleId*97u)%251u+4u)/255.0, 1);
    return o;
}
struct ReferenceAttributes { float3 position : POSITION; float4 color : COLOR; };
Varyings ReferenceVert(ReferenceAttributes a)
{
    Varyings o;
    o.position = mul(_ClipFromWorld, float4(a.position,1));
    o.color = a.color;
    return o;
}
float4 Frag(Varyings i) : SV_Target { return i.color; }
#endif
