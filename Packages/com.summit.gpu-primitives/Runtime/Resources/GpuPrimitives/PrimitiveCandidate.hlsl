// Bounded compile-time candidates. Uint arithmetic intentionally wraps modulo 2^32.
#define TILE (THREADS * ELEMENTS)
#define BINS (1 << DIGIT_BITS)
StructuredBuffer<uint> _ScanInput, _BlockOffsets, _RadixKeysIn, _RadixValuesIn;
RWStructuredBuffer<uint> _ScanOutput, _BlockSums, _RadixKeysOut, _RadixValuesOut;
RWStructuredBuffer<uint> _RadixGroupHistograms;
StructuredBuffer<uint> _RadixGroupOffsets;
RWStructuredBuffer<uint> _Probe;
uint _Count, _RadixShift, _RadixGroupCount;
groupshared uint totals[THREADS];
groupshared uint digits[TILE], keys[TILE], payloads[TILE];
groupshared uint bins[BINS];
groupshared uint waveBins[(THREADS / 4) * BINS];

// Work-efficient tree over register-reduced thread totals; no single-thread
// loop over every wave. All threads, including padded lanes, reach barriers.
uint ThreadPrefix(uint value, uint tid, uint group)
{
#if USE_WAVE
    uint width = WaveGetLaneCount();
    uint wave = tid / width;
    uint prefix = WavePrefixSum(value);
    uint sum = WaveActiveSum(value);
    totals[tid] = 0;
    GroupMemoryBarrierWithGroupSync();
    if (WaveIsFirstLane()) totals[wave] = sum;
    GroupMemoryBarrierWithGroupSync();
#else
    totals[tid] = value;
    GroupMemoryBarrierWithGroupSync();
#endif
    for (uint stride = 1; stride < THREADS; stride <<= 1)
    {
        uint i = (tid + 1) * stride * 2 - 1;
        if (i < THREADS) totals[i] += totals[i - stride];
        GroupMemoryBarrierWithGroupSync();
    }
    if (tid == 0) { _BlockSums[group] = totals[THREADS - 1]; totals[THREADS - 1] = 0; }
    GroupMemoryBarrierWithGroupSync();
    for (uint down = THREADS / 2; down > 0; down >>= 1)
    {
        uint i = (tid + 1) * down * 2 - 1;
        if (i < THREADS) { uint left = totals[i - down]; totals[i - down] = totals[i]; totals[i] += left; }
        GroupMemoryBarrierWithGroupSync();
    }
#if USE_WAVE
    return prefix + totals[wave];
#else
    return totals[tid];
#endif
}

#if REQUESTED_WAVE_SIZE > 0
[WaveSize(REQUESTED_WAVE_SIZE)]
#endif
[numthreads(THREADS, 1, 1)]
void Scan(uint3 g : SV_GroupID, uint tid : SV_GroupIndex)
{
    uint v[ELEMENTS], sum = 0;
    [unroll] for (uint e = 0; e < ELEMENTS; e++)
    {
        uint i = g.x * TILE + tid * ELEMENTS + e;
        v[e] = i < _Count ? _ScanInput[i] : 0;
        sum += v[e];
    }
    uint prefix = ThreadPrefix(sum, tid, g.x);
    [unroll] for (uint outElement = 0; outElement < ELEMENTS; outElement++)
    {
        uint i = g.x * TILE + tid * ELEMENTS + outElement;
        if (i < _Count) _ScanOutput[i] = prefix;
        prefix += v[outElement];
    }
}

#if REQUESTED_WAVE_SIZE > 0
[WaveSize(REQUESTED_WAVE_SIZE)]
#endif
[numthreads(THREADS, 1, 1)]
void Add(uint3 g : SV_GroupID, uint tid : SV_GroupIndex)
{
    [unroll] for (uint e = 0; e < ELEMENTS; e++)
    {
        uint i = g.x * TILE + tid + e * THREADS;
        if (i < _Count) _ScanOutput[i] += _BlockOffsets[g.x];
    }
}

#if REQUESTED_WAVE_SIZE > 0
[WaveSize(REQUESTED_WAVE_SIZE)]
#endif
[numthreads(THREADS, 1, 1)]
void Reduce(uint3 g : SV_GroupID, uint tid : SV_GroupIndex)
{
    uint sum = 0;
    [unroll] for (uint e = 0; e < ELEMENTS; e++)
    {
        uint i = g.x * TILE + tid + e * THREADS;
        if (i < _Count) sum += _ScanInput[i];
    }
#if USE_WAVE
    sum = WaveActiveSum(sum);
    if (!WaveIsFirstLane()) sum = 0;
#endif
    totals[tid] = sum;
    GroupMemoryBarrierWithGroupSync();
    for (uint stride = THREADS / 2; stride > 0; stride >>= 1)
    {
        if (tid < stride) totals[tid] += totals[tid + stride];
        GroupMemoryBarrierWithGroupSync();
    }
    if (tid == 0) _BlockSums[g.x] = totals[0];
}

#if REQUESTED_WAVE_SIZE > 0
[WaveSize(REQUESTED_WAVE_SIZE)]
#endif
[numthreads(THREADS, 1, 1)]
void Histogram(uint3 g : SV_GroupID, uint tid : SV_GroupIndex)
{
    for (uint b = tid; b < BINS; b += THREADS) bins[b] = 0;
    GroupMemoryBarrierWithGroupSync();
    [unroll] for (uint e = 0; e < ELEMENTS; e++)
    {
        uint i = g.x * TILE + tid + e * THREADS;
        if (i < _Count) InterlockedAdd(bins[(_RadixKeysIn[i] >> _RadixShift) & (BINS - 1)], 1);
    }
    GroupMemoryBarrierWithGroupSync();
    // Digit-major storage allows one hierarchical scan to replace the serial
    // per-digit loop across every group, including the global bin bases.
    for (uint outBin = tid; outBin < BINS; outBin += THREADS)
        _RadixGroupHistograms[outBin * _RadixGroupCount + g.x] = bins[outBin];
}

#if REQUESTED_WAVE_SIZE > 0
[WaveSize(REQUESTED_WAVE_SIZE)]
#endif
[numthreads(THREADS, 1, 1)]
void Scatter(uint3 g : SV_GroupID, uint tid : SV_GroupIndex)
{
#if USE_WAVE
    uint width = WaveGetLaneCount(), wave = tid / width;
    for (uint b = tid; b < BINS; b += THREADS) bins[b] = 0;
    GroupMemoryBarrierWithGroupSync();
    [unroll] for (uint e = 0; e < ELEMENTS; e++)
    {
        uint i = g.x * TILE + e * THREADS + tid;
        bool valid = i < _Count;
        uint key = valid ? _RadixKeysIn[i] : 0;
        uint digit = (key >> _RadixShift) & (BINS - 1), rank = 0;
        [unroll] for (uint b = 0; b < BINS; b++)
        {
            bool match = valid && digit == b;
            uint p = WavePrefixCountBits(match), n = WaveActiveCountBits(match);
            if (WaveIsFirstLane()) waveBins[wave * BINS + b] = n;
            if (match) rank = p;
        }
        GroupMemoryBarrierWithGroupSync();
        if (valid)
        {
            for (uint w = 0; w < wave; w++) rank += waveBins[w * BINS + digit];
            uint dst = _RadixGroupOffsets[digit * _RadixGroupCount + g.x] + bins[digit] + rank;
            _RadixKeysOut[dst] = key; _RadixValuesOut[dst] = _RadixValuesIn[i];
        }
        GroupMemoryBarrierWithGroupSync();
        for (uint b = tid; b < BINS; b += THREADS)
            for (uint w = 0; w < THREADS / width; w++) bins[b] += waveBins[w * BINS + b];
        GroupMemoryBarrierWithGroupSync();
    }
#else
    [unroll] for (uint e = 0; e < ELEMENTS; e++)
    {
        uint local = tid + e * THREADS, i = g.x * TILE + local;
        keys[local] = i < _Count ? _RadixKeysIn[i] : 0;
        payloads[local] = i < _Count ? _RadixValuesIn[i] : 0;
        digits[local] = (keys[local] >> _RadixShift) & (BINS - 1);
    }
    GroupMemoryBarrierWithGroupSync();
    // Independent bin owners emit in source order; no atomic rank can reorder ties.
    for (uint b = tid; b < BINS; b += THREADS)
    {
        uint dst = _RadixGroupOffsets[b * _RadixGroupCount + g.x];
        for (uint i = 0; i < min(TILE, _Count - g.x * TILE); i++)
            if (digits[i] == b) { _RadixKeysOut[dst] = keys[i]; _RadixValuesOut[dst++] = payloads[i]; }
    }
#endif
}

#if REQUESTED_WAVE_SIZE > 0
[WaveSize(REQUESTED_WAVE_SIZE)]
#endif
[numthreads(THREADS, 1, 1)]
void Probe(uint tid : SV_GroupIndex)
{
#if USE_WAVE
    // Executed evidence. Each lane is checked by the host, rather than trusting
    // a source keyword or graphicsShaderLevel as proof of the selected width.
    _Probe[tid] = WaveGetLaneCount();
#else
    _Probe[tid] = 0;
#endif
}
