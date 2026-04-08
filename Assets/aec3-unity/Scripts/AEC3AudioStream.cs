using System;
using UnityEngine;

/// <summary>
/// AEC3 回声消除主控脚本。挂在播放远端音频的 AudioSource 上。
///
/// 架构：
///   音频线程 OnAudioFilterRead → 拦截播放信号 → 混成单声道 → 写入 render 环形缓冲
///   主线程  Update             → 读取 mic + render → AEC3处理（10ms/帧）→ 结果可用
///
/// 与 aec3_wrapper.cpp 对齐的关键约束：
///   1. C++ UpdateFrame 硬编码单通道，所有缓冲均为单声道 short[]
///   2. render / capture / output 长度 = sampleRate / 100（10ms单通道）
///   3. 音频线程内禁止 new / GC，所有缓冲在 Awake 预分配
///   4. AEC3_Process 在主线程调用，音频线程只做写缓冲
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class AEC3AudioStream : MonoBehaviour
{
    [Tooltip("麦克风录制缓冲长度（秒），需大于实际使用时长")]
    public int micClipSeconds = 10;

    [Tooltip("启用 Linear AEC 输出（用于调试/分析，会小幅增加开销）")]
    public bool enableLinearOutput = false;

    // ── 核心处理器 ────────────────────────────────────────────────────────────
    private AEC3Processor _aec;

    // ── 音频参数（Awake 后固定不变） ─────────────────────────────────────────
    private int _sampleRate;
    private int _unitySpeakerChannels; // Unity 播放通道数（可能是2）
    private int _frameSize;            // 单通道 10ms = sampleRate / 100

    // ── 麦克风 ───────────────────────────────────────────────────────────────
    private AudioClip _micClip;
    private int _lastMicPos = 0;

    // ── Render 环形缓冲（音频线程写，主线程读，单声道 short）────────────────
    // 容量 = 4秒，足以吸收帧率抖动；所有操作均通过 volatile int 指针保证可见性
    private short[] _renderRing;
    private int _renderRingSize;
    private int _renderWritePos = 0; // 音频线程写
    private int _renderReadPos = 0; // 主线程读

    // ── 主线程复用缓冲（固定大小，运行期零 GC）──────────────────────────────
    private short[] _renderFrame;   // 单声道 render，长度 = _frameSize
    private short[] _captureFrame;  // 单声道 mic，   长度 = _frameSize
    private short[] _outputFrame;   // 单声道 output，长度 = _frameSize
    private short[] _linearFrame;   // linear 输出，  长度 = AEC3Processor.LinearFrameSize
    private float[] _micTempFloat;  // GetData 临时缓冲（float），长度 = _frameSize

    // ── 属性：外部可读最新一帧的处理结果 ────────────────────────────────────
    /// <summary>最新一帧消回声后的单声道 PCM（long度 = FrameSize）</summary>
    public short[] LatestOutputFrame => _outputFrame;
    /// <summary>最新一帧 Linear AEC 输出（16kHz 单声道 160 samples），需启用 enableLinearOutput</summary>
    public short[] LatestLinearFrame => _linearFrame;
    public int FrameSize => _frameSize;
    public bool IsReady => _aec != null;

    // ── 生命周期 ──────────────────────────────────────────────────────────────

    void Awake()
    {
        _sampleRate = AudioSettings.outputSampleRate;
        _unitySpeakerChannels = AudioSettings.speakerMode == AudioSpeakerMode.Stereo ? 2 : 1;
        _frameSize = _sampleRate / 100; // 10ms 单通道

        // render 环形缓冲：4秒 × 单声道
        _renderRingSize = _sampleRate * 4;
        _renderRing = new short[_renderRingSize];

        // 主线程固定缓冲（单声道，与 C++ 严格对齐）
        _renderFrame = new short[_frameSize];
        _captureFrame = new short[_frameSize];
        _outputFrame = new short[_frameSize];
        _linearFrame = new short[AEC3Processor.LinearFrameSize];
        _micTempFloat = new float[_frameSize];

        try
        {
            // C++ 实际以单通道处理，render/capture 均传 1
            _aec = new AEC3Processor(_sampleRate, renderCh: 1, captureCh: 1);
        }
        catch (Exception e)
        {
            Debug.LogError($"[AEC3AudioStream] 初始化失败: {e.Message}");
            enabled = false;
            return;
        }

        // 启动麦克风（单声道，与 C++ 处理通道对齐）
        _micClip = Microphone.Start(null, true, micClipSeconds, _sampleRate);
        if (_micClip == null)
        {
            Debug.LogError("[AEC3AudioStream] 麦克风启动失败，请检查权限");
            enabled = false;
            return;
        }

        Debug.Log($"[AEC3AudioStream] 初始化完成 " +
                  $"sampleRate={_sampleRate} frameSize={_frameSize} " +
                  $"speakerCh={_unitySpeakerChannels}");
    }

    /// <summary>
    /// 音频线程回调：仅做"播放信号 → 单声道 → 写 render 环形缓冲"。
    /// 绝不 new、绝不调用 AEC3、绝不触发 GC。
    /// </summary>
    void OnAudioFilterRead(float[] data, int channels)
    {
        if (_renderRing == null) return;

        int monoSamples = data.Length / channels;
        int wp = _renderWritePos;

        for (int i = 0; i < monoSamples; i++)
        {
            // 多声道混成单声道
            float mono = 0f;
            for (int c = 0; c < channels; c++)
                mono += data[i * channels + c];
            mono /= channels;

            // float → short，写入环形缓冲
            _renderRing[wp % _renderRingSize] =
                (short)Math.Max(-32768, Math.Min(32767, (int)(mono * 32767f)));
            wp++;
        }

        // volatile 写保证主线程可见
        System.Threading.Volatile.Write(ref _renderWritePos, wp);

        // passthrough：不修改 data，AudioSource 正常播放
    }

    /// <summary>
    /// 主线程：每 Update 消耗所有积累的完整 10ms 帧。
    /// </summary>
    void Update()
    {
        if (_aec == null || _micClip == null) return;

        int micPos = Microphone.GetPosition(null);
        if (micPos < _lastMicPos) _lastMicPos = 0; // 录音缓冲环绕

        int renderAvailable = System.Threading.Volatile.Read(ref _renderWritePos) - _renderReadPos;

        while (true)
        {
            // 检查 mic 和 render 各自是否有足够的一帧数据
            if (micPos - _lastMicPos < _frameSize) break;
            if (renderAvailable < _frameSize) break;

            // 1. 读取 render 帧（单声道）
            int rp = _renderReadPos;
            for (int i = 0; i < _frameSize; i++)
                _renderFrame[i] = _renderRing[(rp + i) % _renderRingSize];
            _renderReadPos += _frameSize;
            renderAvailable -= _frameSize;

            // 2. 读取 mic 帧（单声道 float → short）
            _micClip.GetData(_micTempFloat, _lastMicPos);
            for (int i = 0; i < _frameSize; i++)
                _captureFrame[i] = (short)Math.Max(-32768, Math.Min(32767,
                                        (int)(_micTempFloat[i] * 32767f)));

            // 3. AEC3 处理
            //    render  = 播放信号（远端/本地音乐等），C++ 用作回声参考
            //    capture = 麦克风信号（含回声），C++ 输出消除后的结果到 output
            //    linear  = 可选的线性域中间输出（16kHz）
            short[] linearArg = enableLinearOutput ? _linearFrame : null;
            bool ok = _aec.ProcessFrame(_renderFrame, _captureFrame, _outputFrame, linearArg);

            if (!ok)
            {
                // 处理失败时输出原始 mic 信号，避免静音
                Array.Copy(_captureFrame, _outputFrame, _frameSize);
            }

            // 4. _outputFrame 此时为消回声后的单声道 PCM
            //    可在此处送入编码器 / 网络发送 / 写入录音文件等
            OnFrameProcessed(_outputFrame, _frameSize);

            _lastMicPos += _frameSize;
        }
    }

    /// <summary>
    /// 每帧 AEC3 处理完成后的回调钩子，子类或外部逻辑可在此处消费结果。
    /// 默认实现为空；override 或通过 Action 委托扩展。
    /// </summary>
    protected virtual void OnFrameProcessed(short[] outputPcm, int frameSize)
    {
        // 示例：发送到网络编码器
        // NetworkSender.Send(outputPcm, frameSize);

        // 示例：写入验证录音
        // _wavWriter?.Write(outputPcm, frameSize);
    }

    void OnDestroy()
    {
        Microphone.End(null);
        _aec?.Dispose();
    }
}