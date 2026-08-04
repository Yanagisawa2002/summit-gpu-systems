#include <windows.h>
#include <d3d12.h>
#include <dxgi.h>

#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D12.h"
#include "SummitGpuTimestamps.h"

#include <limits.h>
#include <stdint.h>
#include <string.h>

namespace
{
    const uint32_t kAbiVersion = 2;
    const uint32_t kRingCapacity = 1024;
    const uint32_t kQueriesPerSlot = 2;
    const uint32_t kQueryCount = kRingCapacity * kQueriesPerSlot;
    // Each CPU-poisoned slot owns a cache line so a mapped sentinel write cannot
    // share a writeback line with an adjacent slot still being resolved by the GPU.
    const uint64_t kReadbackStride = 64;
    const uint64_t kReadbackSize = kReadbackStride * kRingCapacity;
    const uint32_t kPayloadMagic = 0x53475450u;
    const uint32_t kCapabilities =
        SGT_CAPABILITY_DIRECT_QUEUE |
        SGT_CAPABILITY_NONBLOCKING_POLL |
        SGT_CAPABILITY_RAW_TICKS |
        SGT_CAPABILITY_STABLE_PAYLOADS |
        SGT_CAPABILITY_PRIVATE_COMPLETION_FENCE;

    enum SlotState
    {
        SlotFree = 0,
        SlotReserved,
        SlotArmed,
        SlotBegun,
        SlotResolveRecorded,
        SlotPending,
        SlotCallbackFailed,
        SlotDeviceLost
    };

    struct EventPayload
    {
        uint32_t slotIndex;
        uint32_t magic;
    };

    struct Slot
    {
        uint32_t generation;
        SlotState state;
        uint64_t token;
        uint64_t userTag;
        uint32_t flags;
        uint32_t deviceGeneration;
        uint64_t fenceValue;
        uint64_t sentinelBegin;
        uint64_t sentinelEnd;
    };

    class ExclusiveLock
    {
    public:
        explicit ExclusiveLock(SRWLOCK* lock) : m_lock(lock)
        {
            AcquireSRWLockExclusive(m_lock);
        }
        ~ExclusiveLock()
        {
            ReleaseSRWLockExclusive(m_lock);
        }
    private:
        SRWLOCK* m_lock;
        ExclusiveLock(const ExclusiveLock&);
        ExclusiveLock& operator=(const ExclusiveLock&);
    };

    SRWLOCK g_lock = SRWLOCK_INIT;
    IUnityInterfaces* g_interfaces = 0;
    IUnityGraphics* g_graphics = 0;
    IUnityGraphicsD3D12v8* g_d3d12 = 0;
    ID3D12QueryHeap* g_queryHeap = 0;
    ID3D12Fence* g_completionFence = 0;
    ID3D12Resource* g_readback = 0;
    uint8_t* g_mappedReadback = 0;
    Slot g_slots[kRingCapacity] = {};
    EventPayload g_payloads[kRingCapacity] = {};
    int32_t g_eventBase = -1;
    int32_t g_rendererType = 0;
    uint32_t g_deviceGeneration = 0;
    uint64_t g_timestampFrequency = 0;
    uint64_t g_nextCompletionFenceValue = 0;
    SIZE_T g_readbackWrittenBegin =
        static_cast<SIZE_T>(kReadbackSize);
    SIZE_T g_readbackWrittenEnd = 0;
    bool g_initialized = false;

    template <typename T>
    void ReleaseCom(T*& object)
    {
        if (object != 0)
        {
            object->Release();
            object = 0;
        }
    }

    bool IsTerminalState(SlotState state)
    {
        return state == SlotCallbackFailed ||
            state == SlotDeviceLost;
    }

    void SetTerminalState(Slot& slot, SlotState state)
    {
        if (slot.state != SlotFree &&
            !IsTerminalState(slot.state) &&
            IsTerminalState(state))
        {
            slot.state = state;
        }
    }

    bool IsDeviceLost()
    {
        if (g_d3d12 == 0)
            return true;
        ID3D12Device* device = g_d3d12->GetDevice();
        return device == 0 ||
            FAILED(device->GetDeviceRemovedReason());
    }

    void SetCallbackOrDeviceFailure(Slot& slot)
    {
        SetTerminalState(
            slot,
            IsDeviceLost() ? SlotDeviceLost : SlotCallbackFailed);
    }

    bool TryAllocateCompletionFenceValue(uint64_t* fenceValue)
    {
        if (fenceValue == 0)
            return false;
        *fenceValue = 0;

        // UINT64_MAX is reserved by ID3D12Fence::GetCompletedValue to report
        // device removal. Zero is the unsignaled sentinel used by this ABI.
        if (g_nextCompletionFenceValue >= UINT64_MAX - 1ull)
            return false;
        const uint64_t candidate = g_nextCompletionFenceValue + 1ull;
        if (candidate == 0 || candidate == UINT64_MAX)
            return false;

        // OnRenderEvent holds g_lock, so allocation and the following queue
        // Signal occur in one globally serialized callback order. Consume the
        // value even if Signal fails; a possibly submitted value is never reused.
        g_nextCompletionFenceValue = candidate;
        *fenceValue = candidate;
        return true;
    }

    void ResetSlot(Slot& slot)
    {
        slot.state = SlotFree;
        slot.token = 0;
        slot.userTag = 0;
        slot.flags = 0;
        slot.deviceGeneration = 0;
        slot.fenceValue = 0;
        slot.sentinelBegin = 0;
        slot.sentinelEnd = 0;
    }

    uint64_t MakeToken(uint32_t slotIndex, uint32_t generation)
    {
        return (static_cast<uint64_t>(generation) << 32) |
            static_cast<uint64_t>(slotIndex + 1u);
    }

    bool DecodeToken(uint64_t token, uint32_t* slotIndex, uint32_t* generation)
    {
        if (token == 0 || slotIndex == 0 || generation == 0)
            return false;
        const uint32_t encodedSlot = static_cast<uint32_t>(token);
        const uint32_t encodedGeneration = static_cast<uint32_t>(token >> 32);
        if (encodedSlot == 0 || encodedSlot > kRingCapacity ||
            encodedGeneration == 0)
            return false;
        *slotIndex = encodedSlot - 1u;
        *generation = encodedGeneration;
        return true;
    }

    bool IsCurrent(const Slot& slot, uint64_t token, uint32_t generation)
    {
        return slot.state != SlotFree &&
            slot.token == token &&
            slot.generation == generation;
    }

    EventPayload* ValidatePayload(void* data)
    {
        if (data == 0)
            return 0;
        const uintptr_t address = reinterpret_cast<uintptr_t>(data);
        const uintptr_t base = reinterpret_cast<uintptr_t>(&g_payloads[0]);
        const uintptr_t end = reinterpret_cast<uintptr_t>(
            &g_payloads[kRingCapacity]);
        if (address < base || address >= end)
            return 0;
        const uintptr_t offset = address - base;
        if ((offset % sizeof(EventPayload)) != 0)
            return 0;
        EventPayload* payload = reinterpret_cast<EventPayload*>(data);
        if (payload->magic != kPayloadMagic ||
            payload->slotIndex >= kRingCapacity ||
            payload != &g_payloads[payload->slotIndex])
            return 0;
        return payload;
    }

    void MarkLiveSlots(SlotState state)
    {
        for (uint32_t i = 0; i < kRingCapacity; ++i)
        {
            SetTerminalState(g_slots[i], state);
        }
    }

    void ReleaseDeviceResources(bool deviceLost)
    {
        if (deviceLost)
            MarkLiveSlots(SlotDeviceLost);
        if (g_readback != 0 && g_mappedReadback != 0)
        {
            D3D12_RANGE writtenRange = { 0, 0 };
            if (g_readbackWrittenBegin < g_readbackWrittenEnd &&
                g_readbackWrittenEnd <=
                    static_cast<SIZE_T>(kReadbackSize))
            {
                writtenRange.Begin = g_readbackWrittenBegin;
                writtenRange.End = g_readbackWrittenEnd;
            }
            g_readback->Unmap(0, &writtenRange);
        }
        g_mappedReadback = 0;
        g_readbackWrittenBegin = static_cast<SIZE_T>(kReadbackSize);
        g_readbackWrittenEnd = 0;
        ReleaseCom(g_readback);
        ReleaseCom(g_completionFence);
        ReleaseCom(g_queryHeap);
        g_timestampFrequency = 0;
        g_nextCompletionFenceValue = 0;
        g_initialized = false;
        g_d3d12 = 0;
    }

    bool CreateDeviceResources()
    {
        if (g_interfaces == 0 || g_graphics == 0)
            return false;
        g_rendererType = static_cast<int32_t>(g_graphics->GetRenderer());
        if (g_rendererType != static_cast<int32_t>(kUnityGfxRendererD3D12))
            return false;
        g_d3d12 = g_interfaces->Get<IUnityGraphicsD3D12v8>();
        if (g_d3d12 == 0)
            return false;
        ID3D12Device* device = g_d3d12->GetDevice();
        if (device == 0)
        {
            g_d3d12 = 0;
            return false;
        }

        D3D12_QUERY_HEAP_DESC queryDesc = {};
        queryDesc.Type = D3D12_QUERY_HEAP_TYPE_TIMESTAMP;
        queryDesc.Count = kQueryCount;
        if (FAILED(device->CreateQueryHeap(
            &queryDesc,
            __uuidof(ID3D12QueryHeap),
            reinterpret_cast<void**>(&g_queryHeap))))
        {
            g_queryHeap = 0;
            g_d3d12 = 0;
            return false;
        }

        if (FAILED(device->CreateFence(
            0,
            D3D12_FENCE_FLAG_NONE,
            __uuidof(ID3D12Fence),
            reinterpret_cast<void**>(&g_completionFence))))
        {
            g_completionFence = 0;
            ReleaseCom(g_queryHeap);
            g_d3d12 = 0;
            return false;
        }
        g_nextCompletionFenceValue = 0;

        D3D12_HEAP_PROPERTIES heap = {};
        heap.Type = D3D12_HEAP_TYPE_READBACK;
        heap.CreationNodeMask = 1;
        heap.VisibleNodeMask = 1;
        D3D12_RESOURCE_DESC buffer = {};
        buffer.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        buffer.Width = kReadbackSize;
        buffer.Height = 1;
        buffer.DepthOrArraySize = 1;
        buffer.MipLevels = 1;
        buffer.SampleDesc.Count = 1;
        buffer.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        if (FAILED(device->CreateCommittedResource(
            &heap,
            D3D12_HEAP_FLAG_NONE,
            &buffer,
            D3D12_RESOURCE_STATE_COPY_DEST,
            0,
            __uuidof(ID3D12Resource),
            reinterpret_cast<void**>(&g_readback))))
        {
            ReleaseCom(g_queryHeap);
            ReleaseCom(g_completionFence);
            g_d3d12 = 0;
            return false;
        }

        void* mapped = 0;
        const D3D12_RANGE readRange = {
            0,
            static_cast<SIZE_T>(kReadbackSize)
        };
        if (FAILED(g_readback->Map(0, &readRange, &mapped)) || mapped == 0)
        {
            ReleaseCom(g_readback);
            ReleaseCom(g_queryHeap);
            ReleaseCom(g_completionFence);
            g_d3d12 = 0;
            return false;
        }
        g_mappedReadback = static_cast<uint8_t*>(mapped);
        ++g_deviceGeneration;
        if (g_deviceGeneration == 0)
            ++g_deviceGeneration;
        g_timestampFrequency = 0;
        g_readbackWrittenBegin = static_cast<SIZE_T>(kReadbackSize);
        g_readbackWrittenEnd = 0;
        g_initialized = true;
        return true;
    }

    void ConfigureEvents()
    {
        if (g_d3d12 == 0 || g_eventBase < 0)
            return;
        UnityD3D12PluginEventConfig recording = {};
        recording.graphicsQueueAccess =
            kUnityD3D12GraphicsQueueAccess_DontCare;
        recording.flags = 0;
        recording.ensureActiveRenderTextureIsBound = false;
        g_d3d12->ConfigureEvent(g_eventBase, &recording);
        g_d3d12->ConfigureEvent(g_eventBase + 1, &recording);

        UnityD3D12PluginEventConfig frequency = {};
        frequency.graphicsQueueAccess =
            kUnityD3D12GraphicsQueueAccess_Allow;
        frequency.flags = 0;
        frequency.ensureActiveRenderTextureIsBound = false;
        g_d3d12->ConfigureEvent(g_eventBase + 2, &frequency);

        UnityD3D12PluginEventConfig completion = {};
        completion.graphicsQueueAccess =
            kUnityD3D12GraphicsQueueAccess_Allow;
        completion.flags =
            kUnityD3D12EventConfigFlag_FlushCommandBuffers |
            kUnityD3D12EventConfigFlag_SyncWorkerThreads;
        completion.ensureActiveRenderTextureIsBound = false;
        g_d3d12->ConfigureEvent(g_eventBase + 3, &completion);
    }

    void UNITY_INTERFACE_API OnGraphicsDeviceEvent(
        UnityGfxDeviceEventType eventType)
    {
        ExclusiveLock lock(&g_lock);
        if (eventType == kUnityGfxDeviceEventInitialize ||
            eventType == kUnityGfxDeviceEventAfterReset)
        {
            ReleaseDeviceResources(g_initialized);
            if (CreateDeviceResources())
                ConfigureEvents();
        }
        else if (eventType == kUnityGfxDeviceEventBeforeReset ||
            eventType == kUnityGfxDeviceEventShutdown)
        {
            ReleaseDeviceResources(true);
        }
    }

    void RecordBegin(EventPayload* payload)
    {
        Slot& slot = g_slots[payload->slotIndex];
        if (IsTerminalState(slot.state))
            return;
        if (!g_initialized || g_d3d12 == 0 || g_queryHeap == 0)
        {
            SetTerminalState(slot, SlotDeviceLost);
            return;
        }
        if (slot.state != SlotArmed)
        {
            SetTerminalState(slot, SlotCallbackFailed);
            return;
        }
        UnityGraphicsD3D12RecordingState recording = {};
        if (!g_d3d12->CommandRecordingState(&recording) ||
            recording.commandList == 0)
        {
            SetCallbackOrDeviceFailure(slot);
            return;
        }
        recording.commandList->EndQuery(
            g_queryHeap,
            D3D12_QUERY_TYPE_TIMESTAMP,
            payload->slotIndex * kQueriesPerSlot);
        slot.state = SlotBegun;
    }

    void RecordEnd(EventPayload* payload)
    {
        Slot& slot = g_slots[payload->slotIndex];
        if (IsTerminalState(slot.state))
            return;
        if (!g_initialized || g_d3d12 == 0 ||
            g_queryHeap == 0 || g_readback == 0)
        {
            SetTerminalState(slot, SlotDeviceLost);
            return;
        }
        if (slot.state != SlotBegun)
        {
            SetTerminalState(slot, SlotCallbackFailed);
            return;
        }
        UnityGraphicsD3D12RecordingState recording = {};
        if (!g_d3d12->CommandRecordingState(&recording) ||
            recording.commandList == 0)
        {
            SetCallbackOrDeviceFailure(slot);
            return;
        }
        const uint32_t queryIndex =
            payload->slotIndex * kQueriesPerSlot;
        const uint64_t readbackOffset =
            static_cast<uint64_t>(payload->slotIndex) * kReadbackStride;
        recording.commandList->EndQuery(
            g_queryHeap,
            D3D12_QUERY_TYPE_TIMESTAMP,
            queryIndex + 1u);
        recording.commandList->ResolveQueryData(
            g_queryHeap,
            D3D12_QUERY_TYPE_TIMESTAMP,
            queryIndex,
            kQueriesPerSlot,
            g_readback,
            readbackOffset);
        slot.state = SlotResolveRecorded;
    }

    void RecordCompletion(EventPayload* payload)
    {
        Slot& slot = g_slots[payload->slotIndex];
        if (IsTerminalState(slot.state))
            return;
        if (!g_initialized || g_d3d12 == 0 || g_completionFence == 0)
        {
            SetTerminalState(slot, SlotDeviceLost);
            return;
        }
        if (slot.state != SlotResolveRecorded)
        {
            SetTerminalState(slot, SlotCallbackFailed);
            return;
        }

        uint64_t fenceValue = 0;
        if (!TryAllocateCompletionFenceValue(&fenceValue))
        {
            SetTerminalState(slot, SlotCallbackFailed);
            return;
        }
        slot.fenceValue = fenceValue;

        ID3D12CommandQueue* queue = g_d3d12->GetCommandQueue();
        if (queue == 0)
        {
            SetCallbackOrDeviceFailure(slot);
            return;
        }
        const HRESULT signalResult =
            queue->Signal(g_completionFence, fenceValue);
        if (FAILED(signalResult))
        {
            SetCallbackOrDeviceFailure(slot);
            return;
        }

        slot.state = SlotPending;
    }

    void RecordFrequency()
    {
        if (!g_initialized || g_d3d12 == 0)
        {
            g_timestampFrequency = 0;
            return;
        }
        ID3D12CommandQueue* queue = g_d3d12->GetCommandQueue();
        uint64_t frequency = 0;
        if (queue == 0 ||
            FAILED(queue->GetTimestampFrequency(&frequency)))
        {
            g_timestampFrequency = 0;
            return;
        }
        g_timestampFrequency = frequency;
    }

    void UNITY_INTERFACE_API OnRenderEvent(int eventId, void* data)
    {
        ExclusiveLock lock(&g_lock);
        if (eventId == g_eventBase + 2)
        {
            RecordFrequency();
            return;
        }
        EventPayload* payload = ValidatePayload(data);
        if (payload == 0)
            return;
        if (eventId == g_eventBase)
            RecordBegin(payload);
        else if (eventId == g_eventBase + 1)
            RecordEnd(payload);
        else if (eventId == g_eventBase + 3)
            RecordCompletion(payload);
        else
            SetTerminalState(
                g_slots[payload->slotIndex],
                SlotCallbackFailed);
    }
}

UNITY_INTERFACE_EXPORT void UNITY_INTERFACE_API UnityPluginLoad(
    IUnityInterfaces* unityInterfaces)
{
    ExclusiveLock lock(&g_lock);
    g_interfaces = unityInterfaces;
    g_graphics =
        unityInterfaces != 0 ? unityInterfaces->Get<IUnityGraphics>() : 0;
    for (uint32_t i = 0; i < kRingCapacity; ++i)
    {
        g_payloads[i].slotIndex = i;
        g_payloads[i].magic = kPayloadMagic;
        ResetSlot(g_slots[i]);
    }
    if (g_graphics != 0)
    {
        g_eventBase = g_graphics->ReserveEventIDRange(4);
        g_graphics->RegisterDeviceEventCallback(OnGraphicsDeviceEvent);
    }
    // Unity can load a plugin after the device initialize callback.
    ReleaseDeviceResources(false);
    if (CreateDeviceResources())
        ConfigureEvents();
}

UNITY_INTERFACE_EXPORT void UNITY_INTERFACE_API UnityPluginUnload()
{
    ExclusiveLock lock(&g_lock);
    if (g_graphics != 0)
        g_graphics->UnregisterDeviceEventCallback(OnGraphicsDeviceEvent);
    ReleaseDeviceResources(true);
    g_eventBase = -1;
    g_rendererType = 0;
    g_graphics = 0;
    g_interfaces = 0;
}

uint32_t SGT_CALL SGT_GetAbiVersion()
{
    return kAbiVersion;
}

void* SGT_CALL SGT_GetRenderEventAndDataFunc()
{
    return reinterpret_cast<void*>(&OnRenderEvent);
}

int32_t SGT_CALL SGT_GetEventIds(SGT_EventIds* eventIds)
{
    if (eventIds == 0)
        return SGT_STATUS_INVALID_ARGUMENT;
    ExclusiveLock lock(&g_lock);
    if (g_eventBase < 0)
        return SGT_STATUS_NOT_INITIALIZED;
    eventIds->begin = g_eventBase;
    eventIds->end = g_eventBase + 1;
    eventIds->frequency = g_eventBase + 2;
    eventIds->completion = g_eventBase + 3;
    return SGT_STATUS_READY;
}

int32_t SGT_CALL SGT_GetSupportInfo(SGT_SupportInfo* supportInfo)
{
    if (supportInfo == 0 ||
        supportInfo->structSize != sizeof(SGT_SupportInfo))
        return SGT_STATUS_INVALID_ARGUMENT;
    ExclusiveLock lock(&g_lock);
    const uint32_t callerSize = supportInfo->structSize;
    memset(supportInfo, 0, sizeof(*supportInfo));
    supportInfo->structSize = callerSize;
    supportInfo->abiVersion = kAbiVersion;
    supportInfo->supported = g_initialized ? 1u : 0u;
    supportInfo->capabilityFlags = g_initialized ? kCapabilities : 0u;
    supportInfo->ringCapacity = kRingCapacity;
    supportInfo->rendererType = g_rendererType;
    supportInfo->deviceGeneration = g_deviceGeneration;
    supportInfo->frequencyReady = g_timestampFrequency != 0 ? 1u : 0u;
    supportInfo->timestampFrequency = g_timestampFrequency;
    return g_initialized ? SGT_STATUS_READY : SGT_STATUS_UNSUPPORTED;
}

int32_t SGT_CALL SGT_AcquireSample(
    uint64_t userTag,
    uint32_t flags,
    uint64_t* token,
    void** payload)
{
    if (userTag == 0 || (flags & ~1u) != 0 ||
        token == 0 || payload == 0)
        return SGT_STATUS_INVALID_ARGUMENT;
    *token = 0;
    *payload = 0;
    ExclusiveLock lock(&g_lock);
    if (!g_initialized)
        return SGT_STATUS_NOT_INITIALIZED;
    for (uint32_t i = 0; i < kRingCapacity; ++i)
    {
        Slot& slot = g_slots[i];
        if (slot.state != SlotFree)
            continue;
        ++slot.generation;
        if (slot.generation == 0)
            ++slot.generation;
        slot.state = SlotReserved;
        slot.token = MakeToken(i, slot.generation);
        slot.userTag = userTag;
        slot.flags = flags;
        slot.deviceGeneration = g_deviceGeneration;
        slot.fenceValue = 0;
        slot.sentinelBegin =
            0xD15EA5ED00000000ull ^ slot.token;
        slot.sentinelEnd =
            0xBAD0F00D00000000ull ^ slot.token;
        const SIZE_T readbackOffset =
            static_cast<SIZE_T>(i) *
            static_cast<SIZE_T>(kReadbackStride);
        uint64_t* timestamps = reinterpret_cast<uint64_t*>(
            g_mappedReadback + readbackOffset);
        timestamps[0] = slot.sentinelBegin;
        timestamps[1] = slot.sentinelEnd;
        const SIZE_T writtenEnd =
            readbackOffset + 2u * sizeof(uint64_t);
        if (readbackOffset < g_readbackWrittenBegin)
            g_readbackWrittenBegin = readbackOffset;
        if (writtenEnd > g_readbackWrittenEnd)
            g_readbackWrittenEnd = writtenEnd;
        MemoryBarrier();
        *token = slot.token;
        *payload = &g_payloads[i];
        return SGT_STATUS_READY;
    }
    return SGT_STATUS_RING_FULL;
}

int32_t SGT_CALL SGT_MarkSubmitted(uint64_t token)
{
    uint32_t slotIndex = 0;
    uint32_t generation = 0;
    if (!DecodeToken(token, &slotIndex, &generation))
        return SGT_STATUS_INVALID_TOKEN;
    ExclusiveLock lock(&g_lock);
    Slot& slot = g_slots[slotIndex];
    if (!IsCurrent(slot, token, generation))
        return SGT_STATUS_INVALID_TOKEN;
    if (slot.state == SlotDeviceLost)
        return SGT_STATUS_DEVICE_LOST;
    if (slot.state == SlotCallbackFailed)
        return SGT_STATUS_CALLBACK_ERROR;
    if (slot.state != SlotReserved)
        return SGT_STATUS_INVALID_ARGUMENT;
    if (!g_initialized ||
        slot.deviceGeneration != g_deviceGeneration)
    {
        SetTerminalState(slot, SlotDeviceLost);
        return SGT_STATUS_DEVICE_LOST;
    }

    // This explicit transition closes the window in which a stale or early
    // render event could attach itself to a newly acquired slot. Begin accepts
    // only Armed, while Cancel remains legal only before submission.
    slot.state = SlotArmed;
    return SGT_STATUS_READY;
}

int32_t SGT_CALL SGT_CancelSample(uint64_t token)
{
    uint32_t slotIndex = 0;
    uint32_t generation = 0;
    if (!DecodeToken(token, &slotIndex, &generation))
        return SGT_STATUS_INVALID_TOKEN;
    ExclusiveLock lock(&g_lock);
    Slot& slot = g_slots[slotIndex];
    if (!IsCurrent(slot, token, generation))
        return SGT_STATUS_INVALID_TOKEN;
    if (slot.state != SlotReserved)
        return SGT_STATUS_INVALID_ARGUMENT;
    ResetSlot(slot);
    return SGT_STATUS_READY;
}

int32_t SGT_CALL SGT_TryConsumeResult(
    uint64_t token,
    SGT_TimestampResult* result)
{
    if (result == 0 || result->structSize != sizeof(SGT_TimestampResult))
        return SGT_STATUS_INVALID_ARGUMENT;
    uint32_t slotIndex = 0;
    uint32_t generation = 0;
    if (!DecodeToken(token, &slotIndex, &generation))
        return SGT_STATUS_INVALID_TOKEN;
    ExclusiveLock lock(&g_lock);
    Slot& slot = g_slots[slotIndex];
    if (!IsCurrent(slot, token, generation))
        return SGT_STATUS_INVALID_TOKEN;
    if (slot.state == SlotReserved ||
        slot.state == SlotArmed ||
        slot.state == SlotBegun ||
        slot.state == SlotResolveRecorded)
        return SGT_STATUS_PENDING;
    if (slot.state == SlotCallbackFailed)
        return SGT_STATUS_CALLBACK_ERROR;
    if (slot.state == SlotDeviceLost)
        return SGT_STATUS_DEVICE_LOST;
    if (slot.state != SlotPending)
        return SGT_STATUS_ERROR;
    if (!g_initialized || g_completionFence == 0)
    {
        SetTerminalState(slot, SlotDeviceLost);
        return SGT_STATUS_DEVICE_LOST;
    }
    if (slot.fenceValue == 0 ||
        slot.fenceValue == UINT64_MAX)
    {
        SetTerminalState(slot, SlotCallbackFailed);
        return SGT_STATUS_CALLBACK_ERROR;
    }
    const uint64_t completedFence =
        g_completionFence->GetCompletedValue();
    if (completedFence == UINT64_MAX)
    {
        SetTerminalState(slot, SlotDeviceLost);
        return SGT_STATUS_DEVICE_LOST;
    }
    if (completedFence < slot.fenceValue)
        return SGT_STATUS_PENDING;
    if (g_mappedReadback == 0)
    {
        SetTerminalState(slot, SlotDeviceLost);
        return SGT_STATUS_DEVICE_LOST;
    }
    if (g_timestampFrequency == 0)
    {
        return SGT_STATUS_FREQUENCY_UNAVAILABLE;
    }

    const uint64_t* timestamps = reinterpret_cast<const uint64_t*>(
        g_mappedReadback +
        static_cast<uint64_t>(slotIndex) * kReadbackStride);
    const uint64_t beginTicks = timestamps[0];
    const uint64_t endTicks = timestamps[1];
    if (beginTicks == slot.sentinelBegin ||
        endTicks == slot.sentinelEnd)
    {
        SetTerminalState(slot, SlotCallbackFailed);
        return SGT_STATUS_CALLBACK_ERROR;
    }
    const uint64_t elapsedTicks =
        endTicks >= beginTicks ? endTicks - beginTicks : 0;
    const uint32_t callerSize = result->structSize;
    memset(result, 0, sizeof(*result));
    result->structSize = callerSize;
    result->flags = slot.flags;
    result->token = slot.token;
    result->userTag = slot.userTag;
    result->beginTicks = beginTicks;
    result->endTicks = endTicks;
    result->elapsedTicks = elapsedTicks;
    result->timestampFrequency = g_timestampFrequency;
    result->elapsedMilliseconds =
        static_cast<double>(elapsedTicks) * 1000.0 /
        static_cast<double>(g_timestampFrequency);
    result->fenceValue = slot.fenceValue;
    result->deviceGeneration = slot.deviceGeneration;
    result->reserved = 0;
    ResetSlot(slot);
    return SGT_STATUS_READY;
}
