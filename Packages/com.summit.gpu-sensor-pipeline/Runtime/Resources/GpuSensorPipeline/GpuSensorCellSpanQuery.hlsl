StructuredBuffer<uint4> _Samples, _Queries;
StructuredBuffer<uint> _BinOffsets, _BinnedIds;
RWStructuredBuffer<uint4> _QueryDigests, _Spans;
RWStructuredBuffer<uint> _BlockEnds;
RWByteAddressBuffer _Arguments;
int _ElementCount, _QueryIndex, _QuantizeIntensity;
groupshared uint spanScan[256];

void SpanBounds(out uint3 low, out uint3 high)
{
    uint4 query = _Queries[_QueryIndex];
    uint radius = min(query.w, 65535u);
    low = query.xyz - min(query.xyz, radius.xxx);
    high = min(query.xyz + radius, 65535u);
}

// One descriptor per intersecting y/z row, containing every candidate x cell.
// A hotspot contributes one integer chunk count, not a serial descriptor loop.
[numthreads(256, 1, 1)]
void BuildSpans(uint3 tid : SV_DispatchThreadID, uint3 gid : SV_GroupID, uint lane : SV_GroupIndex)
{
    uint3 low, high;
    SpanBounds(low, high);
    uint3 first = low >> 10u, last = high >> 10u;
    uint rows = last.y - first.y + 1u;
    uint spanCount = rows * (last.z - first.z + 1u);
    uint begin = 0u, end = 0u;
    if (tid.x < spanCount)
    {
        uint yz = ((first.y + tid.x % rows) << 6u) | ((first.z + tid.x / rows) << 12u);
        begin = _BinOffsets[yz | first.x];
        end = _BinOffsets[(yz | last.x) + 1u];
    }
    uint chunks = (end - begin + 255u) / 256u;
    spanScan[lane] = chunks;
    GroupMemoryBarrierWithGroupSync();
    for (uint offset = 1u; offset < 256u; offset <<= 1u)
    {
        uint add = lane >= offset ? spanScan[lane - offset] : 0u;
        GroupMemoryBarrierWithGroupSync();
        spanScan[lane] += add;
        GroupMemoryBarrierWithGroupSync();
    }
    _Spans[tid.x] = uint4(begin, end, spanScan[lane] - chunks, spanScan[lane]);
    if (lane == 255u) _BlockEnds[gid.x] = spanScan[255];
    if (tid.x == 0u) _QueryDigests[_QueryIndex] = 0u;
}

[numthreads(16, 1, 1)]
void PrepareSpanDispatch(uint lane : SV_GroupIndex)
{
    spanScan[lane] = _BlockEnds[lane];
    GroupMemoryBarrierWithGroupSync();
    for (uint offset = 1u; offset < 16u; offset <<= 1u)
    {
        uint add = lane >= offset ? spanScan[lane - offset] : 0u;
        GroupMemoryBarrierWithGroupSync();
        spanScan[lane] += add;
        GroupMemoryBarrierWithGroupSync();
    }
    _BlockEnds[lane] = spanScan[lane];
    if (lane == 0u)
    {
        _BlockEnds[16] = spanScan[15];
        uint n = max(1u, spanScan[15]);
        _Arguments.Store3(0, uint3(min(n, 65535u), (n + 65534u) / 65535u, 1u));
    }
}

uint SpanMix(uint v)
{
    v ^= v >> 16u; v *= 0x7feb352du;
    v ^= v >> 15u; v *= 0x846ca68bu;
    return v ^ (v >> 16u);
}

void SpanAccumulate(uint4 value)
{
    if (value.x == 0u) return;
    InterlockedAdd(_QueryDigests[_QueryIndex].x, value.x);
    InterlockedXor(_QueryDigests[_QueryIndex].y, value.y);
    InterlockedAdd(_QueryDigests[_QueryIndex].z, value.z);
    InterlockedAdd(_QueryDigests[_QueryIndex].w, value.w);
}

#ifndef SPAN_WAVE_REDUCTION
groupshared uint4 spanPartials[256];
#endif

[numthreads(256, 1, 1)]
void ConsumeSpans(uint3 gid : SV_GroupID, uint lane : SV_GroupIndex)
{
    uint chunk = gid.x + gid.y * 65535u;
    uint2 range = 0u;
    if (chunk < _BlockEnds[16])
    {
        // upper_bound skips empty blocks/spans, including repeated prefix values.
        uint lo = 0u, hi = 16u;
        while (lo < hi)
        {
            uint mid = (lo + hi) >> 1u;
            if (_BlockEnds[mid] <= chunk) lo = mid + 1u;
            else hi = mid;
        }
        uint block = lo;
        uint localChunk = chunk - (block == 0u ? 0u : _BlockEnds[block - 1u]);
        lo = block * 256u; hi = lo + 256u;
        while (lo < hi)
        {
            uint mid = (lo + hi) >> 1u;
            if (_Spans[mid].w <= localChunk) lo = mid + 1u;
            else hi = mid;
        }
        uint4 span = _Spans[lo];
        uint begin = span.x + (localChunk - span.z) * 256u;
        range = uint2(begin, min(span.y, begin + 256u));
    }
    uint4 value = 0u;
    uint cursor = range.x + lane;
    uint3 low, high;
    SpanBounds(low, high);
    if (cursor < range.y)
    {
        uint id = _BinnedIds[cursor];
        if (id < (uint)_ElementCount)
        {
            uint4 sample = _Samples[id];
            if (all(sample.xyz >= low) && all(sample.xyz <= high))
            {
                if (_QuantizeIntensity != 0)
                {
                    uint q = sample.w / 65537u;
                    sample.w = min(q + ((sample.w - q * 65537u) > 32768u ? 1u : 0u), 65535u);
                }
                uint h = SpanMix(SpanMix(SpanMix(SpanMix(SpanMix(id ^ 0x85ebca6bu) ^ sample.x) ^ sample.y) ^ sample.z) ^ sample.w);
                value = uint4(1u, h, h, SpanMix(h ^ id ^ 0x27d4eb2fu));
            }
        }
    }
    // No early returns: padded groups, tombstones and partial chunks participate.
#ifdef SPAN_WAVE_REDUCTION
    value = uint4(WaveActiveSum(value.x), WaveActiveBitXor(value.y),
        WaveActiveSum(value.z), WaveActiveSum(value.w));
    if (WaveIsFirstLane()) SpanAccumulate(value);
#else
    spanPartials[lane] = value;
    GroupMemoryBarrierWithGroupSync();
    for (uint stride = 128u; stride != 0u; stride >>= 1u)
    {
        if (lane < stride)
        {
            uint4 right = spanPartials[lane + stride];
            spanPartials[lane].x += right.x; spanPartials[lane].y ^= right.y;
            spanPartials[lane].z += right.z; spanPartials[lane].w += right.w;
        }
        GroupMemoryBarrierWithGroupSync();
    }
    if (lane == 0u) SpanAccumulate(spanPartials[0]);
#endif
}
