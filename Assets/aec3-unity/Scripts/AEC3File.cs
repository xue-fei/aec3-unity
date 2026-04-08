using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// 离线 AEC3 文件处理器。将 lpb.wav (参考信号) 和 mic.wav (录制信号) 进行回声消除，输出 out.wav。
/// 要求：两文件必须为 16000Hz、单声道、16bit PCM 格式。
/// </summary> 
public class AEC3File : MonoBehaviour
{
    [Header("输入文件路径 (相对于项目根目录)")]
    public string lpbWavPath = "/aec3-unity/lpb.wav";
    public string micWavPath = "/aec3-unity/mic.wav";

    [Header("输出文件路径")]
    public string outWavPath = "/aec3-unity/aec_out.wav";

    private AEC3Processor _aec;
    private const int TargetSampleRate = 16000;
    private const int FrameSize = 160; // 10ms @ 16kHz

    // 在 Inspector 右键点击组件即可手动触发，避免自动运行干扰
    [ContextMenu("▶ 开始处理文件")]
    public void StartProcessing() => StartCoroutine(ProcessOffline());

    private IEnumerator ProcessOffline()
    {

        Debug.Log("[AEC3] 正在加载 WAV 文件...");
        short[] lpbSamples = LoadWavMono16(Application.dataPath + lpbWavPath);
        short[] micSamples = LoadWavMono16(Application.dataPath + micWavPath);

        if (lpbSamples == null || micSamples == null) yield break;

        // 确保长度对齐到 10ms 帧边界
        int totalFrames = Math.Min(lpbSamples.Length, micSamples.Length) / FrameSize;
        if (totalFrames <= 0)
        {
            Debug.LogError("[AEC3] 音频太短，无法构成完整 10ms 帧。");
            yield break;
        }

        Debug.Log($"[AEC3] 初始化 AEC3 (16kHz, Mono)...");
        _aec = new AEC3Processor(TargetSampleRate, renderCh: 1, captureCh: 1);

        short[] outputSamples = new short[totalFrames * FrameSize];
        short[] renderBuf = new short[FrameSize];
        short[] captureBuf = new short[FrameSize];
        short[] outputBuf = new short[FrameSize];

        Debug.Log($"[AEC3] 开始处理 {totalFrames} 帧 ({totalFrames * 10f / 1000f:F2} 秒)...");
        float startTime = Time.realtimeSinceStartup;

        for (int i = 0; i < totalFrames; i++)
        {
            // 提取当前 10ms 帧
            Array.Copy(lpbSamples, i * FrameSize, renderBuf, 0, FrameSize);
            Array.Copy(micSamples, i * FrameSize, captureBuf, 0, FrameSize);

            // 调用 AEC3
            bool ok = _aec.ProcessFrame(renderBuf, captureBuf, outputBuf);
            if (!ok) Array.Copy(captureBuf, outputBuf, FrameSize); // 失败降级

            Array.Copy(outputBuf, 0, outputSamples, i * FrameSize, FrameSize);

            // 每处理 100 帧 yield 一次，防止 Unity 编辑器无响应
            if (i % 100 == 0)
            {
                float progress = i / (float)totalFrames * 100f;
                Debug.Log($"[AEC3] 进度: {progress:F1}% ({i}/{totalFrames})");
                yield return null;
            }
        }
        Debug.Log("[AEC3] 处理完成，正在写入输出文件...");
        SaveWavMono16(Application.dataPath + outWavPath, outputSamples);
        Debug.Log($"[AEC3] ✅ 成功! 耗时 {(Time.realtimeSinceStartup - startTime):F2}s, 已保存至 {outWavPath}");

    }

    #region WAV 读写工具 (16bit Mono PCM)

    private short[] LoadWavMono16(string path)
    {
        if (!File.Exists(path)) { Debug.LogError($"文件不存在: {path}"); return null; }

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var br = new BinaryReader(fs);

        // 跳过 RIFF/WAVE 头，定位到 fmt 和 data 块
        fs.Position = 0;
        if (br.ReadInt32() != 0x46464952) { Debug.LogError("非 WAV 文件 (缺少 RIFF)"); return null; }
        br.ReadInt32(); // 文件大小
        if (br.ReadInt32() != 0x45564157) { Debug.LogError("非 WAVE 格式"); return null; }

        bool foundFmt = false, foundData = false;
        int dataPos = 0, dataSize = 0;

        while (fs.Position < fs.Length && (!foundFmt || !foundData))
        {
            int chunkId = br.ReadInt32();
            int chunkSize = br.ReadInt32();

            if (chunkId == 0x20746D66) // "fmt "
            {
                short fmtTag = br.ReadInt16();
                short channels = br.ReadInt16();
                int sampleRate = br.ReadInt32();
                br.ReadInt32(); // avg bytes/sec
                br.ReadInt16(); // block align
                short bitsPerSample = br.ReadInt16();

                if (fmtTag != 1) { Debug.LogError("仅支持 PCM 编码"); return null; }
                if (channels != 1) { Debug.LogError("仅支持单声道"); return null; }
                if (sampleRate != TargetSampleRate) { Debug.LogError($"采样率需为 {TargetSampleRate}Hz，当前为 {sampleRate}Hz"); return null; }
                if (bitsPerSample != 16) { Debug.LogError("仅支持 16bit"); return null; }
                foundFmt = true;
            }
            else if (chunkId == 0x61746164) // "data"
            {
                dataPos = (int)fs.Position;
                dataSize = chunkSize;
                foundData = true;
            }
            else
            {
                fs.Position += chunkSize; // 跳过未知块
            }
        }

        if (!foundFmt || !foundData) { Debug.LogError("WAV 结构异常，缺少 fmt 或 data 块"); return null; }

        int sampleCount = dataSize / 2; // 16bit = 2 bytes/sample
        short[] samples = new short[sampleCount];
        fs.Position = dataPos;
        for (int i = 0; i < sampleCount; i++) samples[i] = br.ReadInt16();

        return samples;
    }

    private void SaveWavMono16(string path, short[] samples)
    {
        string dir = Path.GetDirectoryName(path);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        int dataSize = samples.Length * 2;
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        // RIFF Chunk
        bw.Write(new byte[] { 0x52, 0x49, 0x46, 0x46 }); // "RIFF"
        bw.Write(36 + dataSize); // ChunkSize
        bw.Write(new byte[] { 0x57, 0x41, 0x56, 0x45 }); // "WAVE"

        // fmt Chunk
        bw.Write(new byte[] { 0x66, 0x6D, 0x74, 0x20 }); // "fmt "
        bw.Write(16); // Subchunk1Size
        bw.Write((short)1); // AudioFormat (PCM)
        bw.Write((short)1); // NumChannels
        bw.Write(TargetSampleRate); // SampleRate
        bw.Write(TargetSampleRate * 2); // ByteRate
        bw.Write((short)2); // BlockAlign
        bw.Write((short)16); // BitsPerSample

        // data Chunk
        bw.Write(new byte[] { 0x64, 0x61, 0x74, 0x61 }); // "data"
        bw.Write(dataSize); // Subchunk2Size
        foreach (short s in samples) bw.Write(s);
    }

    #endregion

    void OnDestroy() => _aec?.Dispose();
}