using Copilot.AIService;
using Copilot.Core;
using Core = Copilot.Core;
using Proto = Copilot.Contracts;
using Xunit;

namespace Copilot.Tests;

public class ConfigTests
{
    [Fact]
    public void 能找到仓库根()
    {
        var root = Config.FindRepoRoot();
        Assert.NotNull(root);
        Assert.True(Directory.Exists(Path.Combine(root!, "voice-corpus")));
    }

    [Fact]
    public void 相对路径解析到仓库资源()
    {
        var config = new Config();

        var manifest = config.ResolvePath("voice-corpus/manifest.json");
        var words = config.ResolvePath("data/sensitive_words.txt");

        Assert.True(File.Exists(manifest));
        Assert.True(File.Exists(words));
    }

    [Fact]
    public void 缺省配置下语音库与敏感词都能定位()
    {
        var config = Config.Load();

        Assert.Equal(72, VoiceLib.Load(config.ResolvePath(config.Tts.VoicelibManifest)).Items.Count);
        Assert.Equal(8, SafetyFilter.FromFile(config.ResolvePath(config.Safety.WordsFile)).Count);
    }

    [Fact]
    public void 模型目录下的文件解析到models目录()
    {
        var config = new Config { ModelsDir = "models" };
        var p = config.ModelFile("qwen2.5-7b-instruct-q4_k_m.gguf");

        Assert.Contains(Path.Combine("models", ""), p);
        Assert.EndsWith("qwen2.5-7b-instruct-q4_k_m.gguf", p);
    }
}

public class ProtoMapperTests
{
    [Fact]
    public void 事件映射_指令字段完整保留()
    {
        var ev = Core.TurnEvent.Drive(new Core.DriveAction
        {
            Intent = Intents.TurnRight,
            Steering = 0.6f,
            Throttle = 0.3f,
            HoldMs = 1200,
            Confidence = 0.9f,
        });

        var proto = ProtoMapper.ToProto(ev);

        Assert.Equal(Proto.TurnEvent.PayloadOneofCase.DriveAction, proto.PayloadCase);
        Assert.Equal(Intents.TurnRight, proto.DriveAction.Intent);
        Assert.Equal(0.6f, proto.DriveAction.Steering, 3);
        Assert.Equal(1200, proto.DriveAction.HoldMs);
    }

    [Fact]
    public void 事件映射_音频带采样率与语音库标志()
    {
        var ev = Core.TurnEvent.Tts(new byte[] { 1, 2, 3, 4 }, 24000, true, "行，左转交给我");

        var proto = ProtoMapper.ToProto(ev);

        Assert.Equal(Proto.TurnEvent.PayloadOneofCase.TtsAudio, proto.PayloadCase);
        Assert.Equal(4, proto.TtsAudio.Pcm16.Length);
        Assert.Equal(24000, proto.TtsAudio.SampleRate);
        Assert.True(proto.TtsAudio.FromVoicelib);
    }

    [Fact]
    public void 事件映射_状态增量带情绪()
    {
        var ev = Core.TurnEvent.StateChanged(new Core.StateDelta
        {
            Trust = 55f,
            Emotion = Core.Emotion.Nervous,
            TrustDelta = -5f,
        });

        var proto = ProtoMapper.ToProto(ev);

        Assert.Equal(Proto.Emotion.Nervous, proto.StateDelta.Emotion);
        Assert.Equal(-5f, proto.StateDelta.TrustDelta, 3);
    }

    [Fact]
    public void 游戏状态双向映射_遥测与场景不丢字段()
    {
        var proto = new Proto.GameState
        {
            Trust = 72f,
            Emotion = Proto.Emotion.Happy,
            LevelId = "L1",
            Scene = new Proto.SceneContext { SceneId = "crossroad", Weather = "rain", TimeOfDay = "night" },
            Telemetry = new Proto.VehicleTelemetry
            {
                SpeedKmh = 42f,
                Gear = 3,
                AtIntersection = true,
                ObstacleSummary = "前方红灯;左侧有车",
            },
        };

        var domain = ProtoMapper.ToDomain(proto);
        var back = ProtoMapper.ToProto(domain);

        Assert.Equal(72f, domain.Trust, 3);
        Assert.Equal(Core.Emotion.Happy, domain.Emotion);
        Assert.Equal(42f, domain.Telemetry.SpeedKmh, 3);
        Assert.Equal("前方红灯;左侧有车", domain.Telemetry.ObstacleSummary);
        Assert.Equal("night", back.Scene.TimeOfDay);
        Assert.Equal(3, back.Telemetry.Gear);
    }
}

public class AiRuntimeTests
{
    [Fact]
    public void 降级链在缺少模型时仍然可用()
    {
        // 钉死规则后端：测试不依赖权重文件，也不占用 GPU
        var config = Config.Load();
        config.Llm.Backend = "rule";
        config.Asr.Backend = "none";

        using var runtime = AiRuntime.Create(config);

        Assert.Equal("rule", runtime.Llm.Name);
        Assert.Equal("voicelib", runtime.Tts.Name);
        Assert.Null(runtime.Asr);
        Assert.Equal(72, runtime.VoiceLib.Items.Count);
        Assert.Equal(8, runtime.Safety.Count);
    }

    [Fact]
    public async Task 运行时跑通一轮文本指令()
    {
        var config = Config.Load();
        config.Llm.Backend = "rule";
        config.Asr.Backend = "none";
        using var runtime = AiRuntime.Create(config);

        var events = await runtime.Pipeline.RunTextTurnAsync("靠边停车", runtime.CurrentState);

        Assert.Contains(events, e => e.Kind == TurnEventKind.DriveAction);
        Assert.Contains(events, e => e.Kind == TurnEventKind.TtsAudio && e.FromVoiceLib);
    }

    [Fact]
    public void SyncGameState覆盖本地信任与情绪()
    {
        var config = Config.Load();
        config.Llm.Backend = "rule";
        config.Asr.Backend = "none";
        using var runtime = AiRuntime.Create(config);

        runtime.UpdateState(new GameStateSnapshot { Trust = 12f, Emotion = Emotion.Annoyed });

        Assert.Equal(12f, runtime.Pipeline.Trust.Trust, 3);
        Assert.Equal(Emotion.Annoyed, runtime.Pipeline.Trust.Emotion);
        Assert.Equal(12f, runtime.CurrentState.Trust, 3);
    }

    [Fact]
    public void 显式指定rule后端时不尝试加载模型()
    {
        var config = Config.Load();
        config.Llm.Backend = "rule";

        using var runtime = AiRuntime.Create(config);
        Assert.Equal("rule", runtime.Llm.Name);
    }
}