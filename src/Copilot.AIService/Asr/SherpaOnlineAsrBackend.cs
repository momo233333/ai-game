using System.Buffers.Binary;
using Copilot.Core;
using SherpaOnnx;

namespace Copilot.AIService.Asr;

/// <summary>ASR 后端抽象（IT方案1.md V2.0 §5.1）。</summary>
public interface IAsrBackend
{
    string Name { get; }
    bool IsAvailable { get; }
    int SampleRate { get; }

    /// <summary>对一段 16-bit 单声道 PCM 做识别，返回文本。</summary>
    Task<string> TranscribeAsync(byte[] pcm16, CancellationToken ct = default);
}

/// <summary>
/// 流式 paraformer（sherpa-onnx，ONNX 版与 V1.0 FunASR 同体系）。
/// 两种用法共用同一个 recognizer：
/// ① <see cref="TranscribeAsync"/>：整段喂入取最终结果（离线/一次性）；
/// ② <see cref="CreateSession"/>：流式会话，边喂边出 partial 字幕（麦克风 / 长音频）。
/// </summary>
public sealed class SherpaOnlineAsrBackend : IAsrBackend, IStreamingAsr, IDisposable
{
    private readonly OnlineRecognizer _recognizer;

    public string Name => "sherpa-onnx";
    public bool IsAvailable => true;
    public int SampleRate { get; }

    private SherpaOnlineAsrBackend(OnlineRecognizer recognizer, int sampleRate)
    {
        _recognizer = recognizer;
        SampleRate = sampleRate;
    }

    public static SherpaOnlineAsrBackend? TryCreate(Config.AsrOptions options, string modelDir, out string reason)
    {
        if (!Directory.Exists(modelDir))
        {
            reason = $"ASR 模型目录不存在：{modelDir}";
            return null;
        }

        string? encoder = PickFile(modelDir, "encoder.int8.onnx", "encoder.onnx");
        string? decoder = PickFile(modelDir, "decoder.int8.onnx", "decoder.onnx");
        string? tokens = PickFile(modelDir, "tokens.txt");

        if (encoder == null || decoder == null || tokens == null)
        {
            reason = $"ASR 模型文件不全（需 encoder/decoder/tokens）：{modelDir}";
            return null;
        }

        try
        {
            var config = new OnlineRecognizerConfig();
            config.FeatConfig.SampleRate = options.SampleRate;
            config.FeatConfig.FeatureDim = 80;
            config.ModelConfig.Paraformer.Encoder = encoder;
            config.ModelConfig.Paraformer.Decoder = decoder;
            config.ModelConfig.Tokens = tokens;
            config.ModelConfig.NumThreads = options.NumThreads;
            config.ModelConfig.Provider = options.Provider;
            config.ModelConfig.ModelType = "paraformer";
            config.DecodingMethod = "greedy_search";
            config.EnableEndpoint = 1;

            var recognizer = new OnlineRecognizer(config);
            reason = string.Empty;
            return new SherpaOnlineAsrBackend(recognizer, options.SampleRate);
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return null;
        }
    }

    public Task<string> TranscribeAsync(byte[] pcm16, CancellationToken ct = default)
    {
        var samples = ToFloatSamples(pcm16);
        var stream = _recognizer.CreateStream();
        try
        {
            stream.AcceptWaveform(SampleRate, samples);
            stream.InputFinished();

            while (_recognizer.IsReady(stream))
            {
                ct.ThrowIfCancellationRequested();
                _recognizer.Decode(stream);
            }

            return Task.FromResult(_recognizer.GetResult(stream).Text ?? string.Empty);
        }
        finally
        {
            stream.Dispose();
        }
    }

    /// <summary>创建流式识别会话：麦克风/长音频可边喂边出 partial 字幕（<see cref="IStreamingAsr"/>）。</summary>
    public IAsrSession CreateSession() => new SherpaAsrSession(_recognizer, SampleRate);

    /// <summary>
    /// 单个流式会话。注意 sherpa-onnx 的 <c>OnlineStream</c> 是「一句话」的载体：
    /// 说完一句就要 Dispose 再 CreateStream，不能跨句复用。
    /// </summary>
    private sealed class SherpaAsrSession : IAsrSession
    {
        private readonly OnlineRecognizer _recognizer;
        private readonly OnlineStream _stream;
        private readonly int _sampleRate;
        private bool _finished;

        public SherpaAsrSession(OnlineRecognizer recognizer, int sampleRate)
        {
            _recognizer = recognizer;
            _sampleRate = sampleRate;
            _stream = recognizer.CreateStream();
        }

        public string Accept(byte[] pcm16)
        {
            if (_finished || pcm16 == null || pcm16.Length < 2) return string.Empty;

            _stream.AcceptWaveform(_sampleRate, SherpaOnlineAsrBackend.ToFloatSamples(pcm16));
            while (_recognizer.IsReady(_stream)) _recognizer.Decode(_stream);
            return _recognizer.GetResult(_stream).Text ?? string.Empty;
        }

        public bool IsEndpoint => !_finished && _recognizer.IsEndpoint(_stream);

        public string Finish()
        {
            if (_finished) return string.Empty;
            _finished = true;

            _stream.InputFinished();
            while (_recognizer.IsReady(_stream)) _recognizer.Decode(_stream);
            return _recognizer.GetResult(_stream).Text ?? string.Empty;
        }

        public void Dispose() => _stream.Dispose();
    }

    internal static float[] ToFloatSamples(byte[] pcm16)
    {
        int count = pcm16.Length / 2;
        var samples = new float[count];
        for (int i = 0; i < count; i++)
        {
            short s = BinaryPrimitives.ReadInt16LittleEndian(pcm16.AsSpan(i * 2, 2));
            samples[i] = s / 32768f;
        }
        return samples;
    }

    private static string? PickFile(string dir, params string[] names)
    {
        foreach (var n in names)
        {
            var p = Path.Combine(dir, n);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public void Dispose() => _recognizer.Dispose();
}

/// <summary>ASR 后端工厂：auto 时 sherpa → 不可用（返回 null，由调用方报错）。</summary>
public static class AsrBackendFactory
{
    public static IAsrBackend? Create(Config config, out string detail)
    {
        var backend = (config.Asr.Backend ?? "auto").Trim().ToLowerInvariant();
        if (backend == "none")
        {
            detail = "已禁用 ASR";
            return null;
        }

        var modelDir = config.ModelFile(config.Asr.ModelDir);
        var asr = SherpaOnlineAsrBackend.TryCreate(config.Asr, modelDir, out var reason);
        if (asr != null)
        {
            detail = $"sherpa-onnx（{Path.GetFileName(modelDir)}，{config.Asr.Provider}）";
            return asr;
        }

        if (backend == "sherpa")
        {
            throw new InvalidOperationException($"指定 sherpa 后端但不可用：{reason}");
        }

        detail = $"ASR 不可用（{reason}），仅支持文本/调试模式";
        return null;
    }
}