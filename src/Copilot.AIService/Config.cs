using System.Text.Json;

namespace Copilot.AIService;

/// <summary>
/// 配置外置（IT方案1.md V2.0 §10）：后端选择、量化、阈值、路由开关全部可改，
/// 玩家低配可自行降级，无需重编译。
/// </summary>
public sealed class Config
{
    public string ModelsDir { get; set; } = "models";
    public LlmOptions Llm { get; set; } = new();
    public AsrOptions Asr { get; set; } = new();
    public VadOptions Vad { get; set; } = new();
    public TtsOptions Tts { get; set; } = new();
    public SafetyOptions Safety { get; set; } = new();
    public ServerOptions Server { get; set; } = new();
    public AudioOptions Audio { get; set; } = new();

    /// <summary>本机音频设备（用 `--devices` 看下标）。</summary>
    public sealed class AudioOptions
    {
        /// <summary>录音设备下标；默认 0（系统默认）。选错设备会录到静音 → ASR 输出幻觉文本。</summary>
        public int DeviceNumber { get; set; }
    }

    /// <summary>配置来源路径（诊断用）。</summary>
    public string LoadedFrom { get; private set; } = "(defaults)";

    public sealed class LlmOptions
    {
        /// <summary>auto | llamasharp | rule</summary>
        public string Backend { get; set; } = "auto";
        public string ModelPath { get; set; } = "qwen2.5-7b-instruct-q4_k_m-00001-of-00002.gguf";
        public int ContextSize { get; set; } = 4096;
        public int GpuLayers { get; set; } = 99;
        public int Threads { get; set; } = 8;
        /// <summary>一个 JSON 对象够用；实测模型会一路生成到上限，值越大延迟越高。</summary>
        public int MaxTokens { get; set; } = 128;

        /// <summary>结构化分类任务，温度要低；实测 0.7 会让 7B 随机把正常指令判成 IGNORE。</summary>
        public float Temperature { get; set; } = 0.3f;

        /// <summary>
        /// 是否用 GBNF 文法约束解码（见 <see cref="Copilot.Core.Llm.OutputGrammar"/>）。
        /// 开启后输出必然是合法 JSON、intent 必然是枚举值、action 必然是第一个字段。
        /// </summary>
        public bool UseGrammar { get; set; } = true;

        /// <summary>把 llama.cpp 的原生日志（加载模型时上百行）打到 stderr，默认关闭。</summary>
        public bool VerboseLogs { get; set; }

        /// <summary>把 LLM 的原始输出打到 stderr（排查字段顺序 / 结构化输出问题时用）。</summary>
        public bool DebugRawOutput { get; set; }

        /// <summary>
        /// 实测（RX 9070 XT / Vulkan / Qwen2.5-7B Q4_K_M）：**打开反而慢 2.5 倍**
        /// （35 tok/s → 关闭后 86–90 tok/s）。默认关闭，别按直觉改回 true。
        /// </summary>
        public bool FlashAttention { get; set; }

        /// <summary>
        /// 实测 Windows+Vulkan 下 mmap 会让 `token_embd.weight` 回退到 CPU；
        /// 关闭后冷启动 prefill 1071ms → 695ms。代价是启动时整模型读进内存（约 4.4GB）。
        /// </summary>
        public bool UseMemorymap { get; set; }

        public uint BatchSize { get; set; } = 512;
    }

    public sealed class AsrOptions
    {
        /// <summary>auto | sherpa | text</summary>
        public string Backend { get; set; } = "auto";
        public string ModelDir { get; set; } = "sherpa-onnx-streaming-paraformer-bilingual-zh-en";
        public int SampleRate { get; set; } = 16000;
        public int NumThreads { get; set; } = 2;
        public string Provider { get; set; } = "cpu";
    }

    public sealed class VadOptions
    {
        /// <summary>auto | silero | none</summary>
        public string Backend { get; set; } = "auto";
        public string ModelPath { get; set; } = "silero_vad.onnx";
        public float Threshold { get; set; } = 0.5f;
        public float MinSilenceDuration { get; set; } = 0.6f;
        public float MinSpeechDuration { get; set; } = 0.25f;
    }

    public sealed class TtsOptions
    {
        /// <summary>auto | voicelib | vits</summary>
        public string Backend { get; set; } = "auto";
        public string VoicelibManifest { get; set; } = "voice-corpus/manifest.json";
        public int SampleRate { get; set; } = 24000;
        public VitsOptions Vits { get; set; } = new();

        public sealed class VitsOptions
        {
            public string Model { get; set; } = "vits-zh/model.onnx";
            public string Tokens { get; set; } = "vits-zh/tokens.txt";
            public string Lexicon { get; set; } = "vits-zh/lexicon.txt";
            public int NumThreads { get; set; } = 2;
        }
    }

    public sealed class SafetyOptions
    {
        public string WordsFile { get; set; } = "data/sensitive_words.txt";
    }

    public sealed class ServerOptions
    {
        public string Url { get; set; } = "http://127.0.0.1:50051";
    }

    public static Config Load(string? path = null)
    {
        var config = new Config();

        var candidate = path ?? FindDefaultSettings();
        if (candidate != null && File.Exists(candidate))
        {
            try
            {
                var json = File.ReadAllText(candidate);
                var loaded = JsonSerializer.Deserialize<Config>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
                if (loaded != null)
                {
                    config = loaded;
                    config.LoadedFrom = Path.GetFullPath(candidate);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[config] 读取 {candidate} 失败，使用默认值：{ex.Message}");
            }
        }

        return config;
    }

    private static string? FindDefaultSettings()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (File.Exists(beside)) return beside;

        var cwd = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
        if (File.Exists(cwd)) return cwd;

        var src = Path.Combine(Directory.GetCurrentDirectory(), "src", "Copilot.AIService", "appsettings.json");
        return File.Exists(src) ? src : null;
    }

    /// <summary>解析相对路径：优先绝对路径 → 仓库根 → 当前目录 → 程序目录。</summary>
    public string ResolvePath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return relative;
        if (Path.IsPathRooted(relative)) return relative;

        foreach (var root in new[] { FindRepoRoot(), Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var p = Path.GetFullPath(Path.Combine(root, relative));
            if (File.Exists(p) || Directory.Exists(p)) return p;
        }

        return Path.GetFullPath(Path.Combine(FindRepoRoot() ?? Directory.GetCurrentDirectory(), relative));
    }

    /// <summary>模型目录下的文件（模型权重统一放在 modelsDir）。</summary>
    public string ModelFile(string name) => ResolvePath(Path.Combine(ModelsDir, name));

    /// <summary>从程序目录向上找仓库根（含 IT方案1.md / voice-corpus 的目录）。</summary>
    public static string? FindRepoRoot(string? start = null)
    {
        var dir = new DirectoryInfo(start ?? AppContext.BaseDirectory);
        for (int i = 0; i < 10 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "IT方案1.md")) ||
                Directory.Exists(Path.Combine(dir.FullName, "voice-corpus")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }
}