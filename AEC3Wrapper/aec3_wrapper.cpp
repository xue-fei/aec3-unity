#include "aec3_wrapper.h"
#include "api/echo_canceller3_factory.h"
#include "api/echo_canceller3_config.h"
#include "audio_processing/audio_buffer.h"
#include "audio_processing/high_pass_filter.h"
#include <memory>
#include <cstring>
#include <cstdlib>

using namespace webrtc;

// 1. 补全 FatalLog 解决 LNK2019（运行时几乎不触发，仅防御性断言）
namespace rtc {
    namespace webrtc_checks_impl {
        void FatalLog(const char* file, int line) {
            fprintf(stderr, "[AEC3 FATAL] Check failed at %s:%d\n", file, line);
            std::abort();
        }
    }
}

// 2. 上下文结构体（替代 demo.cc 中的局部变量）
struct AEC3Context {
    std::unique_ptr<EchoControl> echo_control;
    std::unique_ptr<HighPassFilter> hp_filter;
    std::unique_ptr<AudioBuffer> ref_buffer;
    std::unique_ptr<AudioBuffer> cap_buffer;
    std::unique_ptr<AudioBuffer> linear_buffer;
    int sample_rate;
    int channels;
};

extern "C" {

    AEC3_API AEC3Handle AEC3_Create(int sample_rate, int render_ch, int capture_ch) {
        EchoCanceller3Config cfg;
        cfg.filter.export_linear_aec_output = true; // 对齐 demo.cc

        auto ctx = new AEC3Context();
        ctx->sample_rate = sample_rate;
        ctx->channels = capture_ch;

        ctx->echo_control = EchoCanceller3Factory(cfg).Create(sample_rate, render_ch, capture_ch);
        ctx->hp_filter = std::make_unique<HighPassFilter>(sample_rate, capture_ch);

        StreamConfig cfg_stream(sample_rate, capture_ch, false);
        ctx->ref_buffer = std::make_unique<AudioBuffer>(
            cfg_stream.sample_rate_hz(), cfg_stream.num_channels(),
            cfg_stream.sample_rate_hz(), cfg_stream.num_channels(),
            cfg_stream.sample_rate_hz(), cfg_stream.num_channels());
        ctx->cap_buffer = std::make_unique<AudioBuffer>(
            cfg_stream.sample_rate_hz(), cfg_stream.num_channels(),
            cfg_stream.sample_rate_hz(), cfg_stream.num_channels(),
            cfg_stream.sample_rate_hz(), cfg_stream.num_channels());

        constexpr int kLinearRate = 16000;
        ctx->linear_buffer = std::make_unique<AudioBuffer>(
            kLinearRate, capture_ch, kLinearRate, capture_ch, kLinearRate, capture_ch);

        return static_cast<AEC3Handle>(ctx);
    }

    AEC3_API void AEC3_Destroy(AEC3Handle handle) {
        if (handle) delete static_cast<AEC3Context*>(handle);
    }

    AEC3_API int AEC3_GetFrameSize(int sample_rate) {
        return sample_rate / 100; // 固定 10ms 帧
    }

    AEC3_API int AEC3_Process(AEC3Handle handle,
        const short* render_data,
        const short* capture_data,
        short* output_data,
        short* linear_output,
        int samples_per_frame) {
        if (!handle || !render_data || !capture_data || !output_data) return -1;

        auto* ctx = static_cast<AEC3Context*>(handle);
        AudioFrame ref_frame, cap_frame;

        // 1. 填充 AudioFrame（对齐 demo.cc 逻辑）
        ref_frame.UpdateFrame(0, const_cast<int16_t*>(render_data), samples_per_frame,
            ctx->sample_rate, AudioFrame::kNormalSpeech, AudioFrame::kVadActive, 1);
        cap_frame.UpdateFrame(0, const_cast<int16_t*>(capture_data), samples_per_frame,
            ctx->sample_rate, AudioFrame::kNormalSpeech, AudioFrame::kVadActive, 1);

        ctx->ref_buffer->CopyFrom(&ref_frame);
        ctx->cap_buffer->CopyFrom(&cap_frame);

        // 2. 核心 AEC3 处理流（完全复刻 demo.cc）
        ctx->ref_buffer->SplitIntoFrequencyBands();
        ctx->echo_control->AnalyzeRender(ctx->ref_buffer.get());
        ctx->ref_buffer->MergeFrequencyBands();

        ctx->echo_control->AnalyzeCapture(ctx->cap_buffer.get());
        ctx->cap_buffer->SplitIntoFrequencyBands();
        ctx->hp_filter->Process(ctx->cap_buffer.get(), true);
        ctx->echo_control->SetAudioBufferDelay(0); // 实际项目需根据音频管线延迟动态设置
        ctx->echo_control->ProcessCapture(ctx->cap_buffer.get(), ctx->linear_buffer.get(), false);
        ctx->cap_buffer->MergeFrequencyBands();

        // 3. 输出结果
        ctx->cap_buffer->CopyTo(&cap_frame);
        memcpy(output_data, cap_frame.data(), samples_per_frame * ctx->channels * sizeof(short));

        // 4. 可选输出 Linear AEC（固定 16kHz）
        if (linear_output) {
            constexpr int kLinearFrameSize = 160; // 16000 / 100
            cap_frame.UpdateFrame(0, nullptr, kLinearFrameSize, 16000,
                AudioFrame::kNormalSpeech, AudioFrame::kVadActive, 1);
            ctx->linear_buffer->CopyTo(&cap_frame);
            memcpy(linear_output, cap_frame.data(), kLinearFrameSize * ctx->channels * sizeof(short));
        }

        return 0;
    }

} // extern "C"