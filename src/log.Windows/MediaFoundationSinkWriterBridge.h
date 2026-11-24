#pragma once

#include <windows.h>

#include <cstdint>

#ifdef LOG_MEDIA_FOUNDATION_BRIDGE_EXPORTS
#define LOG_MEDIA_FOUNDATION_BRIDGE_API extern "C" __declspec(dllexport)
#else
#define LOG_MEDIA_FOUNDATION_BRIDGE_API extern "C" __declspec(dllimport)
#endif

LOG_MEDIA_FOUNDATION_BRIDGE_API HRESULT WINAPI log_mf_encoder_create(
    const wchar_t* output_path,
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t frame_rate_numerator,
    std::uint32_t frame_rate_denominator,
    std::uint32_t average_bitrate,
    BOOL enable_hardware_transforms,
    void** encoder);

LOG_MEDIA_FOUNDATION_BRIDGE_API HRESULT WINAPI log_mf_encoder_write_bgra(
    void* encoder,
    const std::uint8_t* bgra,
    std::uint32_t byte_length,
    LONGLONG sample_time,
    LONGLONG sample_duration);

LOG_MEDIA_FOUNDATION_BRIDGE_API HRESULT WINAPI log_mf_encoder_finalize(void* encoder);

// Returns 0 when transform inspection is unavailable, 1 for a hardware MFT,
// and 2 for a non-hardware MFT. The result is diagnostic only.
LOG_MEDIA_FOUNDATION_BRIDGE_API HRESULT WINAPI log_mf_encoder_get_backend(
    void* encoder,
    std::int32_t* backend);

LOG_MEDIA_FOUNDATION_BRIDGE_API void WINAPI log_mf_encoder_release(void* encoder) noexcept;
