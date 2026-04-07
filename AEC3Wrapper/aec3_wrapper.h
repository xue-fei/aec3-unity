#pragma once

#ifdef AEC3_WRAPPER_EXPORTS
#define AEC3_API __declspec(dllexport)
#else
#define AEC3_API __declspec(dllimport)
#endif

#ifdef __cplusplus
extern "C" {
#endif

    typedef void* AEC3Handle;

    // 创建实例（参数与 demo.cc 一致）
    AEC3_API AEC3Handle AEC3_Create(int sample_rate, int render_channels, int capture_channels);
    // 销毁实例
    AEC3_API void AEC3_Destroy(AEC3Handle handle);
    // 获取 10ms 帧长（样本数）
    AEC3_API int AEC3_GetFrameSize(int sample_rate);
    // 处理单帧音频
    // render/capture/output 长度必须为 samples_per_frame * channels
    // linear_output 固定为 16kHz * 10ms = 160 样本/通道，可为 nullptr
    AEC3_API int AEC3_Process(AEC3Handle handle,
        const short* render_data,
        const short* capture_data,
        short* output_data,
        short* linear_output,
        int samples_per_frame);

#ifdef __cplusplus
}
#endif