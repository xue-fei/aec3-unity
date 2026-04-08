using System;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// AEC3 Native Plugin 的 C# 封装。
///
/// 与 aec3_wrapper.cpp 的对齐说明：
///   - AEC3_Process 的 samples_per_frame 传单通道帧大小（sampleRate / 100）
///   - C++ 内部 UpdateFrame 硬编码单通道，实际只支持 mono 处理
///   - render / capture / output 缓冲长度均为 frameSize（单通道）个 short
///   - linear 输出固定 160 samples（16kHz × 10ms），可选
/// </summary>
public class AEC3Processor : IDisposable
{
    private IntPtr _handle;

    // 单通道 10ms 帧大小（= sampleRate / 100）
    private readonly int _frameSize;

    // Linear AEC 输出固定 160 samples（16kHz × 10ms × 单通道）
    public const int LinearFrameSize = 160;

#if UNITY_ANDROID && !UNITY_EDITOR
    private const string Lib = "AEC3Wrapper";
#elif UNITY_IOS && !UNITY_EDITOR
    private const string Lib = "__Internal";
#else
    private const string Lib = "AEC3Wrapper";
#endif

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr AEC3_Create(int sampleRate, int renderCh, int captureCh);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void AEC3_Destroy(IntPtr handle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AEC3_GetFrameSize(int sampleRate);

    // samples = 单通道帧大小，render/capture/output 长度均须 >= samples
    // linear 可为 null，若非 null 长度须 >= LinearFrameSize (160)
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AEC3_Process(
        IntPtr handle,
        short[] render,
        short[] capture,
        short[] output,
        short[] linear,
        int samples);

    /// <param name="sampleRate">采样率，需与 Unity outputSampleRate 一致</param>
    /// <param name="renderCh">render（播放）通道数，C++ 内部实际以单通道处理</param>
    /// <param name="captureCh">capture（麦克风）通道数，C++ 内部实际以单通道处理</param>
    public AEC3Processor(int sampleRate, int renderCh = 1, int captureCh = 1)
    {
        _frameSize = AEC3_GetFrameSize(sampleRate); // = sampleRate / 100
        _handle = AEC3_Create(sampleRate, renderCh, captureCh);

        if (_handle == IntPtr.Zero)
            throw new Exception("[AEC3] AEC3_Create 返回空句柄，Native Plugin 可能未正确加载");

        Debug.Log($"[AEC3Processor] 创建成功 sampleRate={sampleRate} frameSize={_frameSize}");
    }

    /// <summary>
    /// 处理一帧音频。
    ///
    /// 参数长度要求（与 C++ wrapper 严格对齐）：
    ///   render  : 长度 >= FrameSize （单通道 10ms 播放信号）
    ///   capture : 长度 >= FrameSize （单通道 10ms 麦克风信号）
    ///   output  : 长度 >= FrameSize （单通道 10ms 消回声后的信号，原地写入）
    ///   linear  : 长度 >= LinearFrameSize=160，或传 null 忽略
    /// </summary>
    public bool ProcessFrame(short[] render, short[] capture, short[] output, short[] linear = null)
    {
        if (_handle == IntPtr.Zero) return false;

        // 防御性长度检查，避免 C++ 越界崩溃
        if (render.Length < _frameSize ||
            capture.Length < _frameSize ||
            output.Length < _frameSize)
        {
            Debug.LogError(
                $"[AEC3Processor] 缓冲长度不足: " +
                $"render={render.Length} capture={capture.Length} output={output.Length} " +
                $"期望每个 >= {_frameSize}");
            return false;
        }

        if (linear != null && linear.Length < LinearFrameSize)
        {
            Debug.LogError($"[AEC3Processor] linear 缓冲长度不足: {linear.Length} < {LinearFrameSize}");
            return false;
        }

        int ret = AEC3_Process(_handle, render, capture, output, linear, _frameSize);
        if (ret != 0)
        {
            Debug.LogWarning($"[AEC3Processor] AEC3_Process 返回错误码: {ret}");
            return false;
        }
        return true;
    }

    /// <summary>单通道 10ms 帧大小（samples）</summary>
    public int FrameSize => _frameSize;

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            AEC3_Destroy(_handle);
            _handle = IntPtr.Zero;
        }
    }
}