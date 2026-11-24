#include "MediaFoundationSinkWriterBridge.h"

#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mftransform.h>
#include <wrl/client.h>
#include <codecapi.h>

#include <cstring>
#include <cstddef>
#include <cwchar>
#include <filesystem>
#include <limits>
#include <memory>
#include <new>
#include <string>
#include <system_error>

namespace
{
using Microsoft::WRL::ComPtr;

HRESULT ensure_output_path_not_reparse_point(
    const wchar_t* output_path,
    std::filesystem::path& normalized_path) noexcept
{
    if (output_path == nullptr || output_path[0] == L'\0')
    {
        return E_INVALIDARG;
    }

    try
    {
        std::error_code path_error;
        normalized_path = std::filesystem::absolute(
            std::filesystem::path(output_path),
            path_error)
            .lexically_normal();
        if (path_error)
        {
            return HRESULT_FROM_WIN32(static_cast<DWORD>(path_error.value()));
        }

        auto current_path = normalized_path;
        while (!current_path.empty())
        {
            const auto attributes = GetFileAttributesW(current_path.c_str());
            if (attributes != INVALID_FILE_ATTRIBUTES)
            {
                if ((attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
                {
                    return HRESULT_FROM_WIN32(ERROR_REPARSE_TAG_INVALID);
                }
            }
            else
            {
                const auto error = GetLastError();
                if (error != ERROR_FILE_NOT_FOUND && error != ERROR_PATH_NOT_FOUND)
                {
                    return error == ERROR_SUCCESS
                        ? E_UNEXPECTED
                        : HRESULT_FROM_WIN32(error);
                }
            }

            const auto parent_path = current_path.parent_path();
            if (parent_path.empty() || parent_path == current_path)
            {
                break;
            }

            current_path = parent_path;
        }
    }
    catch (const std::bad_alloc&)
    {
        return E_OUTOFMEMORY;
    }
    catch (...)
    {
        return E_FAIL;
    }

    return S_OK;
}

bool is_regular_mp4_output_path(const std::filesystem::path& path) noexcept
{
    try
    {
        if (_wcsicmp(path.extension().c_str(), L".mp4") != 0)
        {
            return false;
        }

        const auto native_path = path.native();
        const auto root_name = path.root_name().native();
        const auto root_colon = root_name.find_last_of(L':');
        for (auto colon = native_path.find(L':');
             colon != std::filesystem::path::string_type::npos;
             colon = native_path.find(L':', colon + 1))
        {
            if (root_colon == std::filesystem::path::string_type::npos
                || colon != root_colon)
            {
                return false;
            }
        }
    }
    catch (...)
    {
        return false;
    }

    return true;
}

HRESULT choose_temporary_output_path(
    const std::filesystem::path& output_path,
    std::filesystem::path& temporary_path) noexcept
{
    static volatile LONG sequence = 0;

    try
    {
        const auto parent_path = output_path.parent_path();
        const auto output_name = output_path.filename().native();
        for (unsigned long attempt = 0; attempt < 64; ++attempt)
        {
            const auto serial = static_cast<unsigned long>(InterlockedIncrement(&sequence));
            auto candidate_name = output_name;
            candidate_name += L".logtmp-";
            candidate_name += std::to_wstring(GetCurrentProcessId());
            candidate_name += L"-";
            candidate_name += std::to_wstring(GetTickCount64());
            candidate_name += L"-";
            candidate_name += std::to_wstring(serial + attempt);
            candidate_name += L".mp4";

            const auto candidate = parent_path / candidate_name;
            if (GetFileAttributesW(candidate.c_str()) != INVALID_FILE_ATTRIBUTES)
            {
                continue;
            }

            const auto error = GetLastError();
            if (error != ERROR_FILE_NOT_FOUND && error != ERROR_PATH_NOT_FOUND)
            {
                return error == ERROR_SUCCESS
                    ? E_UNEXPECTED
                    : HRESULT_FROM_WIN32(error);
            }

            temporary_path = candidate;
            return S_OK;
        }
    }
    catch (const std::bad_alloc&)
    {
        return E_OUTOFMEMORY;
    }
    catch (...)
    {
        return E_FAIL;
    }

    return HRESULT_FROM_WIN32(ERROR_FILE_EXISTS);
}

struct encoder_state
{
    ~encoder_state() noexcept
    {
        sink_writer.Reset();
        if (!promoted && !temporary_path.empty())
        {
            DeleteFileW(temporary_path.c_str());
        }
    }

    ComPtr<IMFSinkWriter> sink_writer;
    std::filesystem::path output_path;
    std::filesystem::path temporary_path;
    DWORD stream_index = 0;
    std::uint32_t width = 0;
    std::uint32_t height = 0;
    std::uint32_t encoded_width = 0;
    std::uint32_t encoded_height = 0;
    std::uint32_t input_byte_length = 0;
    std::uint32_t output_byte_length = 0;
    std::unique_ptr<std::uint8_t[]> nv12_buffer;
    LONGLONG last_sample_time = 0;
    bool has_sample_time = false;
    bool sink_finalized = false;
    bool promoted = false;
    bool finalized = false;
    bool backend_observed = false;
    std::int32_t observed_backend = 0;
};

HRESULT set_video_geometry(IMFMediaType* media_type, std::uint32_t width, std::uint32_t height)
{
    auto hr = MFSetAttributeSize(media_type, MF_MT_FRAME_SIZE, width, height);
    if (FAILED(hr))
    {
        return hr;
    }

    return MFSetAttributeRatio(media_type, MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
}

HRESULT set_video_format(
    IMFMediaType* media_type,
    REFGUID subtype,
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t frame_rate_numerator,
    std::uint32_t frame_rate_denominator)
{
    auto hr = media_type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    if (FAILED(hr))
    {
        return hr;
    }

    hr = media_type->SetGUID(MF_MT_SUBTYPE, subtype);
    if (FAILED(hr))
    {
        return hr;
    }

    hr = media_type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    if (FAILED(hr))
    {
        return hr;
    }

    hr = set_video_geometry(media_type, width, height);
    if (FAILED(hr))
    {
        return hr;
    }

    return MFSetAttributeRatio(
        media_type,
        MF_MT_FRAME_RATE,
        frame_rate_numerator,
        frame_rate_denominator);
}

HRESULT calculate_frame_sizes(
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t& encoded_width,
    std::uint32_t& encoded_height,
    std::uint32_t& input_byte_length,
    std::uint32_t& output_byte_length) noexcept
{
    if (width == 0 || height == 0)
    {
        return E_INVALIDARG;
    }

    if ((width & 1U) != 0 && width == std::numeric_limits<std::uint32_t>::max())
    {
        return E_INVALIDARG;
    }

    if ((height & 1U) != 0 && height == std::numeric_limits<std::uint32_t>::max())
    {
        return E_INVALIDARG;
    }

    encoded_width = width + (width & 1U);
    encoded_height = height + (height & 1U);

    const auto input_pixels = static_cast<std::uint64_t>(width)
        * static_cast<std::uint64_t>(height);
    if (input_pixels > std::numeric_limits<std::uint32_t>::max() / 4U)
    {
        return E_INVALIDARG;
    }

    const auto output_pixels = static_cast<std::uint64_t>(encoded_width)
        * static_cast<std::uint64_t>(encoded_height);
    if (output_pixels > std::numeric_limits<std::uint64_t>::max() - output_pixels / 2U)
    {
        return E_INVALIDARG;
    }

    const auto output_bytes = output_pixels + output_pixels / 2U;
    if (output_bytes > std::numeric_limits<std::uint32_t>::max())
    {
        return E_INVALIDARG;
    }

    input_byte_length = static_cast<std::uint32_t>(input_pixels * 4U);
    output_byte_length = static_cast<std::uint32_t>(output_bytes);
    return S_OK;
}

struct rgb_pixel
{
    int blue = 0;
    int green = 0;
    int red = 0;
};

rgb_pixel read_bgra_pixel(
    const std::uint8_t* bgra,
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t x,
    std::uint32_t y) noexcept
{
    if (x >= width)
    {
        x = width - 1;
    }

    if (y >= height)
    {
        y = height - 1;
    }

    const auto offset = (static_cast<std::size_t>(y) * width + x) * 4U;
    return { bgra[offset], bgra[offset + 1U], bgra[offset + 2U] };
}

std::uint8_t clamp_to_byte(int value) noexcept
{
    if (value < 0)
    {
        return 0;
    }

    if (value > 255)
    {
        return 255;
    }

    return static_cast<std::uint8_t>(value);
}

std::uint8_t to_limited_y(int red, int green, int blue) noexcept
{
    // BT.709 limited-range luma, rounded to the nearest 8-bit sample.
    return clamp_to_byte(((47 * red + 157 * green + 16 * blue + 128) >> 8) + 16);
}

std::uint8_t to_limited_u(int red, int green, int blue) noexcept
{
    return clamp_to_byte(((-26 * red - 86 * green + 112 * blue + 32768) >> 8));
}

std::uint8_t to_limited_v(int red, int green, int blue) noexcept
{
    return clamp_to_byte(((112 * red - 102 * green - 10 * blue + 32768) >> 8));
}

void convert_bgra_to_nv12(
    const std::uint8_t* bgra,
    std::uint32_t input_width,
    std::uint32_t input_height,
    std::uint8_t* nv12,
    std::uint32_t encoded_width,
    std::uint32_t encoded_height) noexcept
{
    for (std::uint32_t y = 0; y < encoded_height; ++y)
    {
        for (std::uint32_t x = 0; x < encoded_width; ++x)
        {
            const auto pixel = read_bgra_pixel(
                bgra,
                input_width,
                input_height,
                x,
                y);
            nv12[static_cast<std::size_t>(y) * encoded_width + x] =
                to_limited_y(pixel.red, pixel.green, pixel.blue);
        }
    }

    const auto chroma_offset = static_cast<std::size_t>(encoded_width)
        * encoded_height;
    for (std::uint32_t y = 0; y < encoded_height; y += 2U)
    {
        for (std::uint32_t x = 0; x < encoded_width; x += 2U)
        {
            const auto top_left = read_bgra_pixel(bgra, input_width, input_height, x, y);
            const auto top_right = read_bgra_pixel(bgra, input_width, input_height, x + 1U, y);
            const auto bottom_left = read_bgra_pixel(bgra, input_width, input_height, x, y + 1U);
            const auto bottom_right = read_bgra_pixel(bgra, input_width, input_height, x + 1U, y + 1U);
            const auto red = (top_left.red + top_right.red + bottom_left.red + bottom_right.red + 2) / 4;
            const auto green = (top_left.green + top_right.green + bottom_left.green + bottom_right.green + 2) / 4;
            const auto blue = (top_left.blue + top_right.blue + bottom_left.blue + bottom_right.blue + 2) / 4;
            const auto destination = chroma_offset
                + static_cast<std::size_t>(y / 2U) * encoded_width
                + x;
            nv12[destination] = to_limited_u(red, green, blue);
            nv12[destination + 1U] = to_limited_v(red, green, blue);
        }
    }
}

HRESULT create_encoder(
    const wchar_t* output_path,
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t frame_rate_numerator,
    std::uint32_t frame_rate_denominator,
    std::uint32_t average_bitrate,
    BOOL enable_hardware_transforms,
    void** encoder) noexcept
{
    if (encoder == nullptr)
    {
        return E_INVALIDARG;
    }

    *encoder = nullptr;
    if (output_path == nullptr
        || output_path[0] == L'\0'
        || width == 0
        || height == 0
        || frame_rate_numerator == 0
        || frame_rate_denominator == 0
        || average_bitrate == 0)
    {
        return E_INVALIDARG;
    }

    std::filesystem::path normalized_output_path;
    auto hr = ensure_output_path_not_reparse_point(output_path, normalized_output_path);
    if (FAILED(hr))
    {
        return hr;
    }

    // Keep direct bridge callers on the same regular-MP4 contract as managed callers.
    if (!is_regular_mp4_output_path(normalized_output_path))
    {
        return E_INVALIDARG;
    }

    if (GetFileAttributesW(normalized_output_path.c_str()) != INVALID_FILE_ATTRIBUTES)
    {
        return HRESULT_FROM_WIN32(ERROR_FILE_EXISTS);
    }

    // An attribute lookup failure is not proof that the target is absent.
    const auto output_path_error = GetLastError();
    if (output_path_error != ERROR_FILE_NOT_FOUND
        && output_path_error != ERROR_PATH_NOT_FOUND)
    {
        return output_path_error == ERROR_SUCCESS
            ? E_UNEXPECTED
            : HRESULT_FROM_WIN32(output_path_error);
    }

    std::filesystem::path temporary_output_path;
    hr = choose_temporary_output_path(normalized_output_path, temporary_output_path);
    if (FAILED(hr))
    {
        return hr;
    }

    std::filesystem::path normalized_temporary_output_path;
    hr = ensure_output_path_not_reparse_point(
        temporary_output_path.c_str(),
        normalized_temporary_output_path);
    if (FAILED(hr))
    {
        return hr;
    }

    std::uint32_t encoded_width = 0;
    std::uint32_t encoded_height = 0;
    std::uint32_t input_byte_length = 0;
    std::uint32_t output_byte_length = 0;
    hr = calculate_frame_sizes(
        width,
        height,
        encoded_width,
        encoded_height,
        input_byte_length,
        output_byte_length);
    if (FAILED(hr))
    {
        return hr;
    }

    ComPtr<IMFAttributes> attributes;
    hr = MFCreateAttributes(&attributes, 1);
    if (FAILED(hr))
    {
        return hr;
    }

    if (attributes.Get() == nullptr)
    {
        return E_UNEXPECTED;
    }

    hr = attributes->SetUINT32(
        MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS,
        enable_hardware_transforms != FALSE ? 1U : 0U);
    if (FAILED(hr))
    {
        return hr;
    }

    ComPtr<IMFSinkWriter> sink_writer;
    hr = MFCreateSinkWriterFromURL(
        normalized_temporary_output_path.c_str(),
        nullptr,
        attributes.Get(),
        &sink_writer);
    if (FAILED(hr))
    {
        return hr;
    }

    if (sink_writer.Get() == nullptr)
    {
        return E_UNEXPECTED;
    }

    const auto cleanup_partial_output = [&]() noexcept
    {
        sink_writer.Reset();
        DeleteFileW(normalized_temporary_output_path.c_str());
    };

    ComPtr<IMFMediaType> output_type;
    hr = MFCreateMediaType(&output_type);
    if (FAILED(hr))
    {
        cleanup_partial_output();
        return hr;
    }

    if (output_type.Get() == nullptr)
    {
        cleanup_partial_output();
        return E_UNEXPECTED;
    }

    hr = set_video_format(
        output_type.Get(),
        MFVideoFormat_H264,
        encoded_width,
        encoded_height,
        frame_rate_numerator,
        frame_rate_denominator);
    if (FAILED(hr))
    {
        cleanup_partial_output();
        return hr;
    }

    hr = output_type->SetUINT32(MF_MT_AVG_BITRATE, average_bitrate);
    if (FAILED(hr))
    {
        cleanup_partial_output();
        return hr;
    }

    hr = output_type->SetUINT32(MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_High);
    if (FAILED(hr))
    {
        cleanup_partial_output();
        return hr;
    }

    DWORD stream_index = 0;
    hr = sink_writer->AddStream(output_type.Get(), &stream_index);
    if (FAILED(hr))
    {
        cleanup_partial_output();
        return hr;
    }

    ComPtr<IMFMediaType> input_type;
    hr = MFCreateMediaType(&input_type);
    if (FAILED(hr))
    {
        cleanup_partial_output();
        return hr;
    }

    if (input_type.Get() == nullptr)
    {
        cleanup_partial_output();
        return E_UNEXPECTED;
    }

    hr = set_video_format(
        input_type.Get(),
        MFVideoFormat_NV12,
        encoded_width,
        encoded_height,
        frame_rate_numerator,
        frame_rate_denominator);
    if (FAILED(hr))
    {
        cleanup_partial_output();
        return hr;
    }

    hr = sink_writer->SetInputMediaType(stream_index, input_type.Get(), nullptr);
    if (FAILED(hr))
    {
        cleanup_partial_output();
        return hr;
    }

    auto state = std::unique_ptr<encoder_state>(new (std::nothrow) encoder_state());
    if (!state)
    {
        cleanup_partial_output();
        return E_OUTOFMEMORY;
    }

    try
    {
        state->output_path = normalized_output_path;
        state->temporary_path = normalized_temporary_output_path;
    }
    catch (const std::bad_alloc&)
    {
        cleanup_partial_output();
        return E_OUTOFMEMORY;
    }

    state->stream_index = stream_index;
    state->width = width;
    state->height = height;
    state->encoded_width = encoded_width;
    state->encoded_height = encoded_height;
    state->input_byte_length = input_byte_length;
    state->output_byte_length = output_byte_length;
    state->nv12_buffer.reset(new (std::nothrow) std::uint8_t[output_byte_length]);
    if (!state->nv12_buffer)
    {
        cleanup_partial_output();
        return E_OUTOFMEMORY;
    }

    hr = sink_writer->BeginWriting();
    if (FAILED(hr))
    {
        cleanup_partial_output();
        return hr;
    }

    state->sink_writer = sink_writer;
    *encoder = state.release();
    return S_OK;
}

HRESULT write_bgra(
    encoder_state* state,
    const std::uint8_t* bgra,
    std::uint32_t byte_length,
    LONGLONG sample_time,
    LONGLONG sample_duration) noexcept
{
    if (state == nullptr
        || bgra == nullptr
        || sample_time < 0
        || sample_duration <= 0
        || sample_time > std::numeric_limits<LONGLONG>::max() - sample_duration)
    {
        return E_INVALIDARG;
    }

    if (state->finalized)
    {
        return MF_E_INVALIDREQUEST;
    }

    if (state->sink_writer.Get() == nullptr)
    {
        return E_UNEXPECTED;
    }

    if (state->has_sample_time && sample_time <= state->last_sample_time)
    {
        return E_INVALIDARG;
    }

    if (byte_length != state->input_byte_length)
    {
        return E_INVALIDARG;
    }

    if (!state->nv12_buffer)
    {
        return E_UNEXPECTED;
    }

    convert_bgra_to_nv12(
        bgra,
        state->width,
        state->height,
        state->nv12_buffer.get(),
        state->encoded_width,
        state->encoded_height);

    ComPtr<IMFMediaBuffer> buffer;
    auto hr = MFCreateMemoryBuffer(state->output_byte_length, &buffer);
    if (FAILED(hr))
    {
        return hr;
    }

    if (buffer.Get() == nullptr)
    {
        return E_UNEXPECTED;
    }

    BYTE* destination = nullptr;
    DWORD max_length = 0;
    DWORD current_length = 0;
    hr = buffer->Lock(&destination, &max_length, &current_length);
    if (FAILED(hr))
    {
        return hr;
    }

    if (destination == nullptr || max_length < state->output_byte_length)
    {
        buffer->Unlock();
        return E_UNEXPECTED;
    }

    std::memcpy(destination, state->nv12_buffer.get(), state->output_byte_length);
    hr = buffer->Unlock();
    if (FAILED(hr))
    {
        return hr;
    }

    hr = buffer->SetCurrentLength(state->output_byte_length);
    if (FAILED(hr))
    {
        return hr;
    }

    ComPtr<IMFSample> sample;
    hr = MFCreateSample(&sample);
    if (FAILED(hr))
    {
        return hr;
    }

    if (sample.Get() == nullptr)
    {
        return E_UNEXPECTED;
    }

    hr = sample->AddBuffer(buffer.Get());
    if (FAILED(hr))
    {
        return hr;
    }

    hr = sample->SetSampleTime(sample_time);
    if (FAILED(hr))
    {
        return hr;
    }

    hr = sample->SetSampleDuration(sample_duration);
    if (FAILED(hr))
    {
        return hr;
    }

    hr = state->sink_writer->WriteSample(state->stream_index, sample.Get());
    if (SUCCEEDED(hr))
    {
        state->last_sample_time = sample_time;
        state->has_sample_time = true;
    }

    return hr;
}

HRESULT observe_backend(
    encoder_state* state,
    std::int32_t& backend) noexcept
{
    if (state == nullptr)
    {
        return E_INVALIDARG;
    }

    backend = state->observed_backend;
    if (state->backend_observed)
    {
        return S_OK;
    }

    state->backend_observed = true;
    if (state->sink_writer.Get() == nullptr)
    {
        return S_OK;
    }

    ComPtr<IMFSinkWriterEx> sink_writer_ex;
    if (FAILED(state->sink_writer.As(&sink_writer_ex)) || sink_writer_ex.Get() == nullptr)
    {
        return S_OK;
    }

    bool found_transform = false;
    bool found_hardware_transform = false;
    for (DWORD transform_index = 0; transform_index < 16; ++transform_index)
    {
        ComPtr<IMFTransform> transform;
        GUID category = GUID_NULL;
        const auto hr = sink_writer_ex->GetTransformForStream(
            state->stream_index,
            transform_index,
            &category,
            transform.GetAddressOf());
        if (hr == MF_E_NOT_FOUND || hr == MF_E_INVALIDINDEX)
        {
            break;
        }

        if (FAILED(hr) || transform.Get() == nullptr)
        {
            continue;
        }

        found_transform = true;
        ComPtr<IMFAttributes> attributes;
        if (SUCCEEDED(transform->GetAttributes(&attributes)) && attributes.Get() != nullptr)
        {
            UINT32 hardware_url_length = 0;
            if (SUCCEEDED(attributes->GetStringLength(
                    MFT_ENUM_HARDWARE_URL_Attribute,
                    &hardware_url_length))
                && hardware_url_length > 0)
            {
                found_hardware_transform = true;
            }
        }
    }

    if (found_transform)
    {
        state->observed_backend = found_hardware_transform ? 1 : 2;
        backend = state->observed_backend;
    }

    return S_OK;
}

HRESULT promote_final_output(encoder_state* state) noexcept
{
    if (state == nullptr
        || state->output_path.empty()
        || state->temporary_path.empty())
    {
        return E_INVALIDARG;
    }

    std::filesystem::path normalized_output_path;
    auto hr = ensure_output_path_not_reparse_point(
        state->output_path.c_str(),
        normalized_output_path);
    if (FAILED(hr))
    {
        return hr;
    }

    if (GetFileAttributesW(normalized_output_path.c_str()) != INVALID_FILE_ATTRIBUTES)
    {
        return HRESULT_FROM_WIN32(ERROR_FILE_EXISTS);
    }

    const auto output_path_error = GetLastError();
    if (output_path_error != ERROR_FILE_NOT_FOUND
        && output_path_error != ERROR_PATH_NOT_FOUND)
    {
        return output_path_error == ERROR_SUCCESS
            ? E_UNEXPECTED
            : HRESULT_FROM_WIN32(output_path_error);
    }

    std::filesystem::path normalized_temporary_path;
    hr = ensure_output_path_not_reparse_point(
        state->temporary_path.c_str(),
        normalized_temporary_path);
    if (FAILED(hr))
    {
        return hr;
    }

    // Without MOVEFILE_REPLACE_EXISTING, a competing destination fails the move.
    if (!MoveFileExW(
        normalized_temporary_path.c_str(),
        normalized_output_path.c_str(),
        MOVEFILE_WRITE_THROUGH))
    {
        const auto error = GetLastError();
        return error == ERROR_SUCCESS
            ? E_UNEXPECTED
            : HRESULT_FROM_WIN32(error);
    }

    state->promoted = true;
    return S_OK;
}
}

LOG_MEDIA_FOUNDATION_BRIDGE_API HRESULT WINAPI log_mf_encoder_create(
    const wchar_t* output_path,
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t frame_rate_numerator,
    std::uint32_t frame_rate_denominator,
    std::uint32_t average_bitrate,
    BOOL enable_hardware_transforms,
    void** encoder)
{
    try
    {
        return create_encoder(
            output_path,
            width,
            height,
            frame_rate_numerator,
            frame_rate_denominator,
            average_bitrate,
            enable_hardware_transforms,
            encoder);
    }
    catch (const std::bad_alloc&)
    {
        return E_OUTOFMEMORY;
    }
    catch (...)
    {
        return E_FAIL;
    }
}

LOG_MEDIA_FOUNDATION_BRIDGE_API HRESULT WINAPI log_mf_encoder_write_bgra(
    void* encoder,
    const std::uint8_t* bgra,
    std::uint32_t byte_length,
    LONGLONG sample_time,
    LONGLONG sample_duration)
{
    try
    {
        return write_bgra(
            static_cast<encoder_state*>(encoder),
            bgra,
            byte_length,
            sample_time,
            sample_duration);
    }
    catch (const std::bad_alloc&)
    {
        return E_OUTOFMEMORY;
    }
    catch (...)
    {
        return E_FAIL;
    }
}

LOG_MEDIA_FOUNDATION_BRIDGE_API HRESULT WINAPI log_mf_encoder_finalize(void* encoder)
{
    if (encoder == nullptr)
    {
        return E_INVALIDARG;
    }

    auto state = static_cast<encoder_state*>(encoder);
    if (state->finalized)
    {
        return S_OK;
    }

    if (!state->sink_finalized && state->sink_writer.Get() == nullptr)
    {
        return E_UNEXPECTED;
    }

    try
    {
        if (!state->sink_finalized)
        {
            auto hresult = state->sink_writer->Finalize();
            if (FAILED(hresult))
            {
                return hresult;
            }

            state->sink_finalized = true;
            state->sink_writer.Reset();
        }

        const auto hresult = promote_final_output(state);
        if (SUCCEEDED(hresult))
        {
            state->finalized = true;
        }

        return hresult;
    }
    catch (const std::bad_alloc&)
    {
        return E_OUTOFMEMORY;
    }
    catch (...)
    {
        return E_FAIL;
    }
}

LOG_MEDIA_FOUNDATION_BRIDGE_API HRESULT WINAPI log_mf_encoder_get_backend(
    void* encoder,
    std::int32_t* backend)
{
    if (backend == nullptr)
    {
        return E_INVALIDARG;
    }

    *backend = 0;
    try
    {
        return observe_backend(
            static_cast<encoder_state*>(encoder),
            *backend);
    }
    catch (const std::bad_alloc&)
    {
        return E_OUTOFMEMORY;
    }
    catch (...)
    {
        return E_FAIL;
    }
}

LOG_MEDIA_FOUNDATION_BRIDGE_API void WINAPI log_mf_encoder_release(void* encoder) noexcept
{
    try
    {
        delete static_cast<encoder_state*>(encoder);
    }
    catch (...)
    {
        // Native cleanup must not unwind across the managed ABI boundary.
    }
}
