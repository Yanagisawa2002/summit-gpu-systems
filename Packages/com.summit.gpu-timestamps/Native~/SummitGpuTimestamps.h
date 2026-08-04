#pragma once

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#define SGT_API extern "C" __declspec(dllexport)
#define SGT_CALL __stdcall
#else
#define SGT_API extern "C"
#define SGT_CALL
#endif

enum SGT_Status
{
    SGT_STATUS_READY = 1,
    SGT_STATUS_PENDING = 0,
    SGT_STATUS_ERROR = -1,
    SGT_STATUS_INVALID_ARGUMENT = -2,
    SGT_STATUS_INVALID_TOKEN = -3,
    SGT_STATUS_RING_FULL = -4,
    SGT_STATUS_NOT_INITIALIZED = -5,
    SGT_STATUS_UNSUPPORTED = -6,
    SGT_STATUS_DEVICE_LOST = -7,
    SGT_STATUS_CALLBACK_ERROR = -8,
    SGT_STATUS_FREQUENCY_UNAVAILABLE = -9
};

enum SGT_CapabilityFlags
{
    SGT_CAPABILITY_DIRECT_QUEUE = 1u << 0,
    SGT_CAPABILITY_NONBLOCKING_POLL = 1u << 1,
    SGT_CAPABILITY_RAW_TICKS = 1u << 2,
    SGT_CAPABILITY_STABLE_PAYLOADS = 1u << 3,
    SGT_CAPABILITY_PRIVATE_COMPLETION_FENCE = 1u << 4
};

#pragma pack(push, 8)
struct SGT_EventIds
{
    int32_t begin;
    int32_t end;
    int32_t frequency;
    int32_t completion;
};

struct SGT_SupportInfo
{
    uint32_t structSize;
    uint32_t abiVersion;
    uint32_t supported;
    uint32_t capabilityFlags;
    uint32_t ringCapacity;
    int32_t rendererType;
    uint32_t deviceGeneration;
    uint32_t frequencyReady;
    uint64_t timestampFrequency;
};

struct SGT_TimestampResult
{
    uint32_t structSize;
    uint32_t flags;
    uint64_t token;
    uint64_t userTag;
    uint64_t beginTicks;
    uint64_t endTicks;
    uint64_t elapsedTicks;
    uint64_t timestampFrequency;
    double elapsedMilliseconds;
    uint64_t fenceValue;
    uint32_t deviceGeneration;
    uint32_t reserved;
};
#pragma pack(pop)

#if defined(__cplusplus)
static_assert(sizeof(SGT_EventIds) == 16, "SGT_EventIds ABI size changed.");
static_assert(offsetof(SGT_EventIds, frequency) == 8, "SGT_EventIds ABI offset changed.");
static_assert(offsetof(SGT_EventIds, completion) == 12, "SGT_EventIds ABI offset changed.");
static_assert(sizeof(SGT_SupportInfo) == 40, "SGT_SupportInfo ABI size changed.");
static_assert(offsetof(SGT_SupportInfo, timestampFrequency) == 32, "SGT_SupportInfo ABI offset changed.");
static_assert(sizeof(SGT_TimestampResult) == 80, "SGT_TimestampResult ABI size changed.");
static_assert(offsetof(SGT_TimestampResult, elapsedMilliseconds) == 56, "SGT_TimestampResult ABI offset changed.");
static_assert(offsetof(SGT_TimestampResult, deviceGeneration) == 72, "SGT_TimestampResult ABI offset changed.");
#endif

SGT_API uint32_t SGT_CALL SGT_GetAbiVersion();
SGT_API void* SGT_CALL SGT_GetRenderEventAndDataFunc();
SGT_API int32_t SGT_CALL SGT_GetEventIds(SGT_EventIds* eventIds);
SGT_API int32_t SGT_CALL SGT_GetSupportInfo(SGT_SupportInfo* supportInfo);
SGT_API int32_t SGT_CALL SGT_AcquireSample(
    uint64_t userTag,
    uint32_t flags,
    uint64_t* token,
    void** payload);
SGT_API int32_t SGT_CALL SGT_MarkSubmitted(uint64_t token);
SGT_API int32_t SGT_CALL SGT_CancelSample(uint64_t token);
SGT_API int32_t SGT_CALL SGT_TryConsumeResult(
    uint64_t token,
    SGT_TimestampResult* result);
