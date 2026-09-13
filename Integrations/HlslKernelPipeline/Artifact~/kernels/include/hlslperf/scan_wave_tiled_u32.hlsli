// Wave-tiled scan adapted from GPUPrefixSums ScanCommon.hlsl and its
// decoupled fallback algorithm, commit 98d93a4e9ed2f3c8353119515bf9be90a2e137ad.
// SPDX-License-Identifier: MIT
// Copyright (c) 2024 Thomas Smith
// Local modifications Copyright (c) 2026 Edwin Liu
// The complete upstream license is retained at third_party/gpu-prefix-sums/LICENSE.
// Local changes: raw-buffer ABI, full-u32 split state, private fallback,
// per-operation state clear, scalar tails, parallel fixed-wave spine, compaction.
// This is an opt-in local adaptation, not the unmodified upstream implementation.
#ifndef HLSLPERF_SCAN_WAVE_TILED_U32_INCLUDED
#define HLSLPERF_SCAN_WAVE_TILED_U32_INCLUDED

#if HLSLPERF_SCAN_WAVE_TILED != 1
#error The wave-tiled entry points require explicit HLSLPERF_SCAN_WAVE_TILED=1.
#endif
#if HLSLPERF_SCAN_BACKEND != 3 || HLSLPERF_VECTOR_WIDTH != 4 || HLSLPERF_SCAN_OPERATOR != 1
#error Wave-tiled scan requires backend 3, vector width 4, and uint32 addition.
#endif
#if HLSLPERF_WAVE_SIZE != 32 && HLSLPERF_WAVE_SIZE != 64
#error Wave-tiled scan requires an explicitly supported fixed wave size of 32 or 64.
#endif
#if HLSLPERF_GROUP_SIZE < HLSLPERF_WAVE_SIZE || HLSLPERF_GROUP_SIZE > 1024 || (HLSLPERF_GROUP_SIZE & (HLSLPERF_GROUP_SIZE - 1)) != 0
#error Wave-tiled scan requires a power-of-two group containing complete waves, at most 1024 threads.
#endif
#if HLSLPERF_SINGLE_PASS_ITEMS_PER_THREAD < 4 || HLSLPERF_SINGLE_PASS_ITEMS_PER_THREAD > 64 || (HLSLPERF_SINGLE_PASS_ITEMS_PER_THREAD % 4) != 0
#error Wave-tiled scan requires 4..64 items per thread, divisible by four.
#endif
#if HLSLPERF_SCAN_DIAGNOSTIC_COUNTERS
#error Wave-tiled scan does not implement diagnostic counters.
#endif
#ifndef HLSLPERF_WAVE_TILED_MAX_POLLS
#define HLSLPERF_WAVE_TILED_MAX_POLLS 4
#endif
#if HLSLPERF_WAVE_TILED_MAX_POLLS < 1 || HLSLPERF_WAVE_TILED_MAX_POLLS > 16
#error Wave-tiled scan requires 1..16 bounded status polls.
#endif

#define HLSLPERF_WAVE_TILED_CHUNKS (HLSLPERF_SINGLE_PASS_ITEMS_PER_THREAD / 4)
#define HLSLPERF_WAVE_TILED_WAVES (HLSLPERF_GROUP_SIZE / HLSLPERF_WAVE_SIZE)

// Engine consumers provide their own binding layout and omit this attribute.
#ifndef HLSLPERF_WAVE_TILED_ROOT_ATTRIBUTE
#define HLSLPERF_WAVE_TILED_ROOT_ATTRIBUTE [RootSignature(HLSLPERF_ROOT_SIGNATURE)]
#endif

groupshared uint WaveTiledSpine[HLSLPERF_WAVE_TILED_WAVES];
groupshared uint WaveTiledReduction;
groupshared uint WaveTiledBlock;
groupshared uint WaveTiledPrefix;
groupshared uint WaveTiledMissing;
groupshared uint WaveTiledDone;

// Same allocation as the legacy state: [reserved, nextBlock], then
// [aggregate, inclusivePrefix, status] per partition. No payload flag packing.
// Reset every operation, with a host UAV barrier before the data dispatch.
// One group clears the complete logical range, so there is no epoch rollover.
HLSLPERF_WAVE_TILED_ROOT_ATTRIBUTE
[numthreads(HLSLPERF_GROUP_SIZE, 1, 1)]
void ResetWaveTiledState(uint groupIndex : SV_GroupIndex)
{
    for (uint block = groupIndex; block < HLSLPERF_SCAN_LOGICAL_BLOCK_COUNT; block += HLSLPERF_GROUP_SIZE)
        Output0.Store(8 + block * 12 + 8, 0);
    if (groupIndex == 0)
        Output0.Store2(0, uint2(0, 0));
}

uint HlslPerfWaveTiledAcquireWave(uint groupIndex)
{
    // SV_GroupIndex need not describe the implementation's wave/lane packing.
    // Allocate one logical segment per wave instead, once per persistent group.
    // Reuse the spine's reduction word; the final barrier ends its counter life.
    if (groupIndex == 0)
        WaveTiledReduction = 0;
    GroupMemoryBarrierWithGroupSync();
    uint wave = 0;
    if (WaveIsFirstLane())
        InterlockedAdd(WaveTiledReduction, 1, wave);
    wave = WaveReadLaneFirst(wave);
    GroupMemoryBarrierWithGroupSync();
    return wave;
}

uint HlslPerfWaveTiledIndex(uint block, uint wave, uint chunk)
{
    return block * ElementsPerBlock + wave * HLSLPERF_WAVE_SIZE * HLSLPERF_SINGLE_PASS_ITEMS_PER_THREAD
        + (chunk * HLSLPERF_WAVE_SIZE + WaveGetLaneIndex()) * 4;
}

uint4 HlslPerfWaveTiledLoad(uint index)
{
    uint4 values = 0;
    if (index < ElementCount && ElementCount - index >= 4)
        values = Input0.Load4(index * 4);
    else
    {
        [unroll]
        for (uint component = 0; component < 4; ++component)
            if (index + component < ElementCount)
                values[component] = Input0.Load((index + component) * 4);
    }
    return values;
}

uint4 HlslPerfWaveTiledValues(uint4 values, uint index)
{
#if HLSLPERF_WAVE_TILED_COMPACTION
    uint4 flags = 0;
    [unroll]
    for (uint component = 0; component < 4; ++component)
        flags[component] = index + component < ElementCount && CompactionPredicate(values[component]) ? 1 : 0;
    return flags;
#else
    return values;
#endif
}

// The first wave scans the wave totals in parallel. Complete waves and
// GROUP_SIZE <= 1024 ensure WAVE_TILED_WAVES <= WAVE_SIZE, including wave32.
// Call uniformly; every caller must consume the results before reusing the spine.
void HlslPerfWaveTiledSpine(uint wave, uint waveTotal, out uint wavePrefix, out uint blockTotal)
{
    const uint lane = WaveGetLaneIndex();
    if (lane == 0)
        WaveTiledSpine[wave] = waveTotal;
    GroupMemoryBarrierWithGroupSync();
    if (wave == 0)
    {
        const uint value = lane < HLSLPERF_WAVE_TILED_WAVES ? WaveTiledSpine[lane] : 0;
        const uint prefix = WavePrefixSum(value);
        const uint total = WaveActiveSum(value);
        if (lane < HLSLPERF_WAVE_TILED_WAVES)
            WaveTiledSpine[lane] = prefix;
        if (lane == 0)
            WaveTiledReduction = total;
    }
    GroupMemoryBarrierWithGroupSync();
    wavePrefix = WaveTiledSpine[wave];
    blockTotal = WaveTiledReduction;
}

uint HlslPerfWaveTiledFallback(uint groupIndex, uint wave, uint predecessor)
{
    uint sum = 0;
    [unroll]
    for (uint chunk = 0; chunk < HLSLPERF_WAVE_TILED_CHUNKS; ++chunk)
    {
        // Reduction has no output-order constraint: use group-striped vectors.
        const uint index = predecessor * ElementsPerBlock + (chunk * HLSLPERF_GROUP_SIZE + groupIndex) * 4;
        const uint4 values = HlslPerfWaveTiledValues(HlslPerfWaveTiledLoad(index), index);
        sum += values.x + values.y + values.z + values.w;
    }
    uint unusedPrefix;
    uint total;
    HlslPerfWaveTiledSpine(wave, WaveActiveSum(sum), unusedPrefix, total);
    return total;
}

void HlslPerfWaveTiledLookback(uint groupIndex, uint wave, uint blockTotal)
{
    uint suffix = 0;
    uint predecessor = WaveTiledBlock;
    if (groupIndex == 0)
    {
        WaveTiledDone = predecessor == 0;
        if (predecessor != 0)
            --predecessor;
        const uint state = 8 + WaveTiledBlock * 12;
        Output1.Store(state, blockTotal);
        DeviceMemoryBarrier();
        uint ignored;
        Output1.InterlockedExchange(state + 8, 1, ignored);
    }
    GroupMemoryBarrierWithGroupSync();

    [loop]
    while (true)
    {
        if (groupIndex == 0)
        {
            uint misses = 0;
            [allow_uav_condition]
            while (WaveTiledDone == 0)
            {
                const uint state = 8 + predecessor * 12;
                uint status;
                Output1.InterlockedCompareExchange(state + 8, 0xffffffffu, 0xffffffffu, status);
                if (status == 2)
                {
                    // Atomics alone are not a memory fence. Output1 must remain
                    // globallycoherent, and the owner never rewrites either payload.
                    DeviceMemoryBarrier();
                    suffix += Output1.Load(state + 4);
                    WaveTiledDone = 1;
                }
                else if (status == 1)
                {
                    DeviceMemoryBarrier();
                    suffix += Output1.Load(state);
                    misses = 0;
                    if (predecessor == 0)
                        WaveTiledDone = 1;
                    else
                        --predecessor;
                }
                else if (++misses == HLSLPERF_WAVE_TILED_MAX_POLLS)
                {
                    WaveTiledMissing = predecessor;
                    break;
                }
            }
            WaveTiledPrefix = suffix;
        }
        GroupMemoryBarrierWithGroupSync();
        if (WaveTiledDone != 0)
            break;

        // No lock and no predecessor publication: even a suspended owner cannot
        // prevent this group from consuming the immutable input and moving left.
        // A concurrent owner publication is harmless; consume this partition once.
        const uint fallbackTotal = HlslPerfWaveTiledFallback(groupIndex, wave, WaveTiledMissing);
        if (groupIndex == 0)
        {
            suffix += fallbackTotal;
            if (predecessor == 0)
                WaveTiledDone = 1;
            else
                --predecessor;
        }
        GroupMemoryBarrierWithGroupSync();
    }

    if (groupIndex == 0)
    {
        const uint state = 8 + WaveTiledBlock * 12;
        Output1.Store(state + 4, suffix + blockTotal);
        DeviceMemoryBarrier();
        uint ignored;
        Output1.InterlockedExchange(state + 8, 2, ignored);
#if HLSLPERF_WAVE_TILED_COMPACTION
        if (WaveTiledBlock + 1 == HLSLPERF_SCAN_LOGICAL_BLOCK_COUNT)
            Output0.Store(0, suffix + blockTotal);
#endif
    }
    GroupMemoryBarrierWithGroupSync();
}

HLSLPERF_WAVE_TILED_ROOT_ATTRIBUTE
HLSLPERF_WAVE_ATTRIBUTE
[numthreads(HLSLPERF_GROUP_SIZE, 1, 1)]
#if HLSLPERF_WAVE_TILED_COMPACTION
void FusedCompactWaveTiled(uint groupIndex : SV_GroupIndex)
#else
void SinglePassScanWaveTiled(uint groupIndex : SV_GroupIndex)
#endif
{
    // Native adapters may dispatch one empty group with at least a 4-byte output.
    // The existing ABI-v1 workload builder still requires a positive item count.
    if (ElementCount == 0)
    {
#if HLSLPERF_WAVE_TILED_COMPACTION
        if (groupIndex == 0)
            Output0.Store(0, 0);
#endif
        return;
    }

    const uint wave = HlslPerfWaveTiledAcquireWave(groupIndex);
    [loop]
    while (true)
    {
        if (groupIndex == 0)
            Output1.InterlockedAdd(4, 1, WaveTiledBlock);
        GroupMemoryBarrierWithGroupSync();
        if (WaveTiledBlock >= HLSLPERF_SCAN_LOGICAL_BLOCK_COUNT)
            break;

        uint4 prefixes[HLSLPERF_WAVE_TILED_CHUNKS];
#if HLSLPERF_WAVE_TILED_COMPACTION
        uint4 inputs[HLSLPERF_WAVE_TILED_CHUNKS];
        uint4 flags[HLSLPERF_WAVE_TILED_CHUNKS];
#endif
        uint waveTotal = 0;
        [unroll]
        for (uint chunk = 0; chunk < HLSLPERF_WAVE_TILED_CHUNKS; ++chunk)
        {
            const uint index = HlslPerfWaveTiledIndex(WaveTiledBlock, wave, chunk);
            const uint4 input = HlslPerfWaveTiledLoad(index);
            const uint4 values = HlslPerfWaveTiledValues(input, index);
#if HLSLPERF_WAVE_TILED_COMPACTION
            inputs[chunk] = input;
            flags[chunk] = values;
#endif
            const uint sum = values.x + values.y + values.z + values.w;
            const uint lanePrefix = WavePrefixSum(sum);
            const uint tilePrefix = waveTotal + lanePrefix;
            prefixes[chunk] = uint4(0, values.x, values.x + values.y, values.x + values.y + values.z) + tilePrefix;
            // Reuse the completed scan for the tile total instead of a second
            // wave reduction (the upstream scan uses the same inclusive-total idea).
            waveTotal += WaveReadLaneAt(lanePrefix + sum, HLSLPERF_WAVE_SIZE - 1);
        }
        uint wavePrefix;
        uint blockTotal;
        HlslPerfWaveTiledSpine(wave, waveTotal, wavePrefix, blockTotal);
        HlslPerfWaveTiledLookback(groupIndex, wave, blockTotal);

        [unroll]
        for (uint storeChunk = 0; storeChunk < HLSLPERF_WAVE_TILED_CHUNKS; ++storeChunk)
        {
            const uint4 output = prefixes[storeChunk] + wavePrefix + WaveTiledPrefix;
#if HLSLPERF_WAVE_TILED_COMPACTION
            [unroll]
            for (uint component = 0; component < 4; ++component)
                if (flags[storeChunk][component] != 0)
                    Output0.Store((output[component] + 1) * 4, inputs[storeChunk][component]);
#else
            const uint index = HlslPerfWaveTiledIndex(WaveTiledBlock, wave, storeChunk);
            if (index < ElementCount && ElementCount - index >= 4)
                Output0.Store4(index * 4, output);
            else
            {
                [unroll]
                for (uint storeComponent = 0; storeComponent < 4; ++storeComponent)
                    if (index + storeComponent < ElementCount)
                        Output0.Store((index + storeComponent) * 4, output[storeComponent]);
            }
#endif
        }
        // Every lane must finish using the shared prefix/index before reuse.
        GroupMemoryBarrierWithGroupSync();
    }
}

#endif
