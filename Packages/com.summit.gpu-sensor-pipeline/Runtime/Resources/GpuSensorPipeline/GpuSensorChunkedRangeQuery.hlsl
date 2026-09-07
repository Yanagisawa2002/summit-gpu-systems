StructuredBuffer<uint4> _Samples;
StructuredBuffer<uint> _BinOffsets;
StructuredBuffer<uint> _BinnedIds;
StructuredBuffer<uint4> _Queries;
RWStructuredBuffer<uint4> _QueryDigests;
RWStructuredBuffer<uint2> _Chunks;
RWStructuredBuffer<uint> _Count;
RWByteAddressBuffer _Arguments;
int _ElementCount;
int _QueryIndex;
int _QuantizeIntensity;

uint MixQuery(uint v)
{
    v ^= v >> 16u; v *= 0x7FEB352Du;
    v ^= v >> 15u; v *= 0x846CA68Bu;
    return v ^ (v >> 16u);
}

void Bounds(out uint3 low, out uint3 high)
{
    uint4 query = _Queries[_QueryIndex];
    uint r = min(query.w, 65535u);
    low = query.xyz - min(query.xyz, r.xxx);
    high = min(query.xyz + r, 65535u);
}

[numthreads(1, 1, 1)]
void ClearQuery()
{
    _Count[0] = 0u;
    _QueryDigests[_QueryIndex] = 0u;
}

// Fixed 16 groups visit only cells intersecting the inclusive query AABB.
// Each nonempty cell reserves exactly ceil(rangeLength / 256) work items.
[numthreads(256, 1, 1)]
void BuildChunks(uint3 tid : SV_DispatchThreadID)
{
    uint3 low, high;
    Bounds(low, high);
    uint3 first = low >> 10u;
    uint3 size = (high >> 10u) - first + 1u;
    uint cells = size.x * size.y * size.z;
    for (uint c = tid.x; c < cells; c += 4096u)
    {
        uint3 cell = first + uint3(c % size.x, (c / size.x) % size.y, c / (size.x * size.y));
        uint key = cell.x | (cell.y << 6u) | (cell.z << 12u);
        uint begin = _BinOffsets[key];
        uint end = _BinOffsets[key + 1u];
        uint length = end - begin;
        uint n = (length + 255u) / 256u;
        if (n == 0u) continue;
        uint baseIndex;
        InterlockedAdd(_Count[0], n, baseIndex);
        for (uint i = 0u; i < n; ++i)
            _Chunks[baseIndex + i] = uint2(begin + i * 256u, min(end, begin + (i + 1u) * 256u));
    }
}

[numthreads(1, 1, 1)]
void PrepareDispatch()
{
    uint n = max(1u, _Count[0]); // Empty queries still dispatch one guarded group.
    _Arguments.Store3(0, uint3(min(n, 65535u), (n + 65534u) / 65535u, 1u));
}

#ifndef QUERY_WAVE_REDUCTION
groupshared uint4 partials[256];
#endif

void Accumulate(uint4 value)
{
    InterlockedAdd(_QueryDigests[_QueryIndex].x, value.x);
    InterlockedXor(_QueryDigests[_QueryIndex].y, value.y);
    InterlockedAdd(_QueryDigests[_QueryIndex].z, value.z);
    InterlockedAdd(_QueryDigests[_QueryIndex].w, value.w);
}

[numthreads(256, 1, 1)]
void ConsumeChunks(uint3 gid : SV_GroupID, uint lane : SV_GroupIndex)
{
    uint chunkIndex = gid.x + gid.y * 65535u;
    // Keep every lane on the barrier path: FXC conservatively treats a UAV
    // counter read as varying, even though chunkIndex is uniform per group.
    uint2 range = 0u;
    if (chunkIndex < _Count[0]) range = _Chunks[chunkIndex];
    uint cursor = range.x + lane;
    uint4 value = 0u;
    uint3 low, high;
    Bounds(low, high);
    if (cursor < range.y)
    {
        uint id = _BinnedIds[cursor];
        if (id < (uint)_ElementCount) // Includes UINT_MAX tombstone rejection.
        {
            uint4 sample = _Samples[id];
            if (all(sample.xyz >= low) && all(sample.xyz <= high))
            {
                if (_QuantizeIntensity != 0)
                {
                    uint q = sample.w / 65537u;
                    sample.w = min(q + ((sample.w - q * 65537u) > 32768u ? 1u : 0u), 65535u);
                }
                uint h = MixQuery(id ^ 0x85EBCA6Bu);
                h = MixQuery(h ^ sample.x);
                h = MixQuery(h ^ sample.y);
                h = MixQuery(h ^ sample.z);
                h = MixQuery(h ^ sample.w);
                value = uint4(1u, h, h, MixQuery(h ^ id ^ 0x27D4EB2Fu));
            }
        }
    }
#ifdef QUERY_WAVE_REDUCTION
    // Native wave width, no Wave32/64 assumption. All lanes participate even in
    // partial chunks. One atomic digest per wave replaces eight group barriers.
    value = uint4(WaveActiveSum(value.x), WaveActiveBitXor(value.y),
        WaveActiveSum(value.z), WaveActiveSum(value.w));
    if (WaveIsFirstLane()) Accumulate(value);
#else
    partials[lane] = value;
    GroupMemoryBarrierWithGroupSync();
    for (uint stride = 128u; stride > 0u; stride >>= 1u)
    {
        if (lane < stride)
        {
            uint4 right = partials[lane + stride];
            partials[lane].x += right.x;
            partials[lane].y ^= right.y;
            partials[lane].z += right.z;
            partials[lane].w += right.w;
        }
        GroupMemoryBarrierWithGroupSync();
    }
    if (lane == 0u) Accumulate(partials[0]);
#endif
}
