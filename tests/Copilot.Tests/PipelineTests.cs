using Copilot.Core;
using Copilot.Core.Dialogue;
using Copilot.Core.Llm;
using Copilot.Core.Tts;
using Xunit;

namespace Copilot.Tests;

public class TurnPipelineTests
{
    private static TurnPipeline NewPipeline()
    {
        var lib = TestPaths.LoadVoiceLib();
        var tts = new TtsRouter(lib);
        return new TurnPipeline(new RuleLlmBackend(), tts, SafetyFilter.Empty);
    }

    [Fact]
    public async Task 事件顺序为_识别_指令_台词_语音_状态()
    {
        var pipeline = NewPipeline();

        var events = await pipeline.RunTextTurnAsync("前面路口左转", new GameStateSnapshot());

        Assert.Equal(new[]
        {
            TurnEventKind.AsrFinal,
            TurnEventKind.DriveAction,
            TurnEventKind.LlmDelta,
            TurnEventKind.TtsAudio,
            TurnEventKind.StateDelta,
        }, events.Select(e => e.Kind).ToArray());
    }

    [Fact]
    public async Task 指令先于台词产出_且带正确动作参数()
    {
        var pipeline = NewPipeline();

        var events = await pipeline.RunTextTurnAsync("前面路口左转", new GameStateSnapshot());
        var driveIndex = events.ToList().FindIndex(e => e.Kind == TurnEventKind.DriveAction);
        var replyIndex = events.ToList().FindIndex(e => e.Kind == TurnEventKind.LlmDelta);

        Assert.True(driveIndex < replyIndex, "drive_action 必须先于台词下发");

        var action = events[driveIndex].Action!;
        Assert.Equal(Intents.TurnLeft, action.Intent);
        Assert.Equal(-0.6f, action.Steering, 3);
    }

    [Fact]
    public async Task 命中语音库_下发真PCM而非空音频()
    {
        var pipeline = NewPipeline();

        var events = await pipeline.RunTextTurnAsync("前面路口左转", new GameStateSnapshot());
        var tts = events.Single(e => e.Kind == TurnEventKind.TtsAudio);

        Assert.True(tts.FromVoiceLib);
        Assert.NotNull(tts.Pcm16);
        Assert.True(tts.Pcm16!.Length > 2000);
        Assert.Equal(24000, tts.SampleRate);
    }

    [Fact]
    public async Task 信任值按回复增量变化()
    {
        var pipeline = NewPipeline();

        var events = await pipeline.RunTextTurnAsync("前面路口左转", new GameStateSnapshot());
        var state = events.Single(e => e.Kind == TurnEventKind.StateDelta).State!;

        Assert.Equal(1f, state.TrustDelta, 3);
        Assert.Equal(61f, state.Trust, 3);
        Assert.Equal(61f, pipeline.Trust.Trust, 3);
    }

    [Fact]
    public async Task 危险指令被拒绝_信任扣5()
    {
        var pipeline = NewPipeline();

        var events = await pipeline.RunTextTurnAsync("直接闯红灯冲过去", new GameStateSnapshot());
        var action = events.Single(e => e.Kind == TurnEventKind.DriveAction).Action!;
        var state = events.Single(e => e.Kind == TurnEventKind.StateDelta).State!;

        Assert.Equal(Intents.Ignore, action.Intent);
        Assert.Equal(-5f, state.TrustDelta, 3);
    }

    [Fact]
    public async Task 敏感词在输入层拦截_不进LLM()
    {
        var lib = TestPaths.LoadVoiceLib();
        var pipeline = new TurnPipeline(new RuleLlmBackend(), new TtsRouter(lib),
            new SafetyFilter(new[] { "去死" }));

        var events = await pipeline.RunTextTurnAsync("你去死吧", new GameStateSnapshot());

        // 短路：不产生任何 LLM 生成的台词，只出预置拒绝线
        Assert.Contains(events, e => e.Kind == TurnEventKind.DriveAction);
        var reply = events.Single(e => e.Kind == TurnEventKind.LlmDelta).Text!;
        Assert.DoesNotContain("去死", reply);
    }

    [Fact]
    public async Task 多个回合累积信任_并受上下限约束()
    {
        var pipeline = NewPipeline();

        for (int i = 0; i < 60; i++)
        {
            await pipeline.RunTextTurnAsync("前面路口左转", new GameStateSnapshot());
        }

        Assert.Equal(100f, pipeline.Trust.Trust, 3);
    }

    [Fact]
    public void 本地事件产生状态事件()
    {
        var pipeline = NewPipeline();

        var ev = pipeline.ApplyLocalEvent("collision");

        Assert.Equal(TurnEventKind.StateDelta, ev.Kind);
        Assert.Equal(-12f, ev.State!.TrustDelta, 3);
    }

    [Fact]
    public async Task 旧账在信任重挫后写入记忆()
    {
        var pipeline = NewPipeline();

        await pipeline.RunTextTurnAsync("直接闯红灯冲过去", new GameStateSnapshot());

        Assert.NotEmpty(pipeline.Memory.Debts);
    }

    /// <summary>
    /// 回归：实测真 LLM 偶尔返回空 reply，导致「妹妹不吭声」且字幕空白。
    /// 约定空台词由语音库按意图兜底，字幕与语音同时补上。
    /// </summary>
    [Fact]
    public async Task 回归_模型返回空台词时用语音库按意图兜底()
    {
        var lib = TestPaths.LoadVoiceLib();
        var stub = new StubLlmBackend(new LlmOutput
        {
            Action = new DriveAction { Intent = Intents.Accelerate },
            Reply = string.Empty,
        });
        var pipeline = new TurnPipeline(stub, new TtsRouter(lib), SafetyFilter.Empty);

        var events = await pipeline.RunTextTurnAsync("踩油门快点", new GameStateSnapshot());

        var reply = events.Single(e => e.Kind == TurnEventKind.LlmDelta).Text;
        Assert.False(string.IsNullOrWhiteSpace(reply));

        var tts = events.Single(e => e.Kind == TurnEventKind.TtsAudio);
        Assert.True(tts.FromVoiceLib);
        Assert.Equal(reply, tts.Text);
    }

    /// <summary>
    /// 回归：真机实测「字幕」与「听到的语音」不是同一句 —— LLM 原文不在语音库里、
    /// 实时合成又不可用时，音频走了「按意图兜底」的台词，字幕却仍显示 LLM 原文。
    /// 约定：音频来自语音库时，字幕必须与之一致。
    /// </summary>
    [Fact]
    public async Task 回归_音频来自语音库时字幕必须与听到的一致()
    {
        var lib = TestPaths.LoadVoiceLib();
        var stub = new StubLlmBackend(new LlmOutput
        {
            Action = new DriveAction { Intent = Intents.TurnLeft },
            Reply = "这是一句语音库里没有的自由发挥台词",
        });
        var pipeline = new TurnPipeline(stub, new TtsRouter(lib), SafetyFilter.Empty);

        var events = await pipeline.RunTextTurnAsync("前面路口左转", new GameStateSnapshot());

        var reply = events.Single(e => e.Kind == TurnEventKind.LlmDelta).Text;
        var tts = events.Single(e => e.Kind == TurnEventKind.TtsAudio);

        Assert.True(tts.FromVoiceLib);
        Assert.Equal(reply, tts.Text);
        Assert.Contains("TURN_LEFT", lib.FindByText(reply!)!.IntentTags);
    }

    /// <summary>有实时合成时（FromVoiceLib=false）必须保留 LLM 原文，不能被兜底文案覆盖。</summary>
    [Fact]
    public async Task 有实时合成时保留LLM原文()
    {
        var lib = TestPaths.LoadVoiceLib();
        var stub = new StubLlmBackend(new LlmOutput
        {
            Action = new DriveAction { Intent = Intents.TurnLeft },
            Reply = "自由发挥的台词",
        });
        var pipeline = new TurnPipeline(stub, new TtsRouter(lib, new FakeRealtimeTts()), SafetyFilter.Empty);

        var events = await pipeline.RunTextTurnAsync("前面路口左转", new GameStateSnapshot());

        var reply = events.Single(e => e.Kind == TurnEventKind.LlmDelta).Text;
        Assert.Equal("自由发挥的台词", reply);
    }

    /// <summary>
    /// 回归（真机实测）：LLM 把「今天天气不错」「谢了」「帮我买个菜」都判成 IGNORE 并每次扣 5 点信任，
    /// 十几轮下来信任从 60 崩到 35。约定：危险判定归代码，非危险输入的 IGNORE 一律降级为反问且不扣信任。
    /// </summary>
    [Theory]
    [InlineData("今天天气不错")]
    [InlineData("谢了")]
    [InlineData("帮我买个菜")]
    [InlineData("不是你知道我在说的是啥吗")]
    public async Task 回归_非危险输入被判IGNORE时降级为反问且不扣信任(string input)
    {
        var lib = TestPaths.LoadVoiceLib();
        var stub = new StubLlmBackend(new LlmOutput
        {
            Action = new DriveAction { Intent = Intents.Ignore },
            Reply = "随便",
            TrustDelta = -5f,
        });
        var pipeline = new TurnPipeline(stub, new TtsRouter(lib), SafetyFilter.Empty);

        var events = await pipeline.RunTextTurnAsync(input, new GameStateSnapshot());

        Assert.Equal(Intents.QuestionBack, events.Single(e => e.Kind == TurnEventKind.DriveAction).Action!.Intent);
        Assert.Equal(0f, events.Single(e => e.Kind == TurnEventKind.StateDelta).State!.TrustDelta, 3);
    }

    [Fact]
    public async Task 真危险指令仍保持IGNORE并扣信任()
    {
        var lib = TestPaths.LoadVoiceLib();
        var stub = new StubLlmBackend(new LlmOutput
        {
            Action = new DriveAction { Intent = Intents.Ignore },
            Reply = "别闹",
            TrustDelta = -5f,
        });
        var pipeline = new TurnPipeline(stub, new TtsRouter(lib), SafetyFilter.Empty);

        var events = await pipeline.RunTextTurnAsync("直接闯红灯冲过去", new GameStateSnapshot());

        Assert.Equal(Intents.Ignore, events.Single(e => e.Kind == TurnEventKind.DriveAction).Action!.Intent);
        Assert.Equal(-5f, events.Single(e => e.Kind == TurnEventKind.StateDelta).State!.TrustDelta, 3);
    }

    private sealed class FakeRealtimeTts : Copilot.Core.Tts.IRealtimeTts
    {
        public string Name => "fake-tts";
        public bool IsAvailable => true;
        public int SampleRate => 22050;
        public byte[]? Synthesize(string text) => new byte[] { 1, 2, 3, 4 };
    }

    private sealed class StubLlmBackend : ILlmBackend
    {
        private readonly LlmOutput _output;
        public StubLlmBackend(LlmOutput output) => _output = output;
        public string Name => "stub";
        public bool IsAvailable => true;
        public Task<LlmOutput> GenerateAsync(LlmRequest request, CancellationToken ct = default)
            => Task.FromResult(_output);
    }

    /// <summary>流式桩：先吐 action，再吐最终结果；final 为 null 时模拟「台词还没生成完就断了」。</summary>
    private sealed class StreamingStub : ILlmBackend, IStreamingLlmBackend
    {
        private readonly DriveAction? _ready;
        private readonly LlmOutput? _final;

        public StreamingStub(DriveAction? ready, LlmOutput? final)
        {
            _ready = ready;
            _final = final;
        }

        public string Name => "stream-stub";
        public bool IsAvailable => true;

        public Task<LlmOutput> GenerateAsync(LlmRequest request, CancellationToken ct = default)
            => Task.FromResult(_final ?? new LlmOutput());

        public async IAsyncEnumerable<LlmStreamChunk> GenerateStreamAsync(
            LlmRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            if (_ready != null) yield return new LlmStreamChunk { ActionReady = _ready };
            await Task.Yield();
            if (_final != null) yield return new LlmStreamChunk { Final = _final };
        }
    }

    /// <summary>
    /// 回归（方案 §4.2）：action 一闭合就该下发 drive_action，
    /// 即使最终结果还没出来（这里桩故意永不产出 Final）——证明指令不依赖整段台词。
    /// </summary>
    [Fact]
    public async Task 流式_action闭合即下发_不等台词生成完()
    {
        var lib = TestPaths.LoadVoiceLib();
        var stub = new StreamingStub(new DriveAction { Intent = Intents.TurnLeft }, final: null);
        var pipeline = new TurnPipeline(stub, new TtsRouter(lib), SafetyFilter.Empty);

        var events = new List<TurnEvent>();
        await foreach (var e in pipeline.RunTextTurnStreamAsync("前面路口左转", new GameStateSnapshot()))
        {
            events.Add(e);
        }

        var action = events.Single(e => e.Kind == TurnEventKind.DriveAction).Action!;
        Assert.Equal(Intents.TurnLeft, action.Intent);
        Assert.Equal(-0.6f, action.Steering, 3);          // 规范参数表已补齐
        Assert.Contains(events, e => e.Kind == TurnEventKind.Error); // 没有 Final → 报错收尾
    }

    [Fact]
    public async Task 流式_指令仍先于台词()
    {
        var lib = TestPaths.LoadVoiceLib();
        var ready = new DriveAction { Intent = Intents.Brake };
        var final = new LlmOutput { Action = ready, Reply = "减速减速，别催" };
        var pipeline = new TurnPipeline(new StreamingStub(ready, final), new TtsRouter(lib), SafetyFilter.Empty);

        var events = new List<TurnEvent>();
        await foreach (var e in pipeline.RunTextTurnStreamAsync("刹车", new GameStateSnapshot()))
        {
            events.Add(e);
        }

        int drive = events.FindIndex(e => e.Kind == TurnEventKind.DriveAction);
        int reply = events.FindIndex(e => e.Kind == TurnEventKind.LlmDelta);

        Assert.True(drive >= 0 && drive < reply, "drive_action 必须先于台词");
        // 提前下发的那一次不能被最终结果再发一遍
        Assert.Single(events, e => e.Kind == TurnEventKind.DriveAction);
    }

    [Fact]
    public async Task 流式与列表版结果一致()
    {
        var lib = TestPaths.LoadVoiceLib();
        var ready = new DriveAction { Intent = Intents.Park };
        var final = new LlmOutput { Action = ready, Reply = "靠边停是吧，好。" };

        var pipeline = new TurnPipeline(new StreamingStub(ready, final), new TtsRouter(lib), SafetyFilter.Empty);
        var streamed = new List<TurnEvent>();
        await foreach (var e in pipeline.RunTextTurnStreamAsync("靠边停车", new GameStateSnapshot()))
        {
            streamed.Add(e);
        }

        var pipeline2 = new TurnPipeline(new StubLlmBackend(final), new TtsRouter(lib), SafetyFilter.Empty);
        var listed = await pipeline2.RunTextTurnAsync("靠边停车", new GameStateSnapshot());

        Assert.Equal(listed.Select(e => e.Kind), streamed.Select(e => e.Kind));
    }
}

public class TtsRouterTests
{
    [Fact]
    public void 未命中语音库但有意图时_按意图兜底下发()
    {
        var router = new TtsRouter(TestPaths.LoadVoiceLib());

        var result = router.Route(null, "这是一句语料库没有的自由发挥台词", Intents.Brake, Emotion.Calm);

        Assert.True(result.FromVoiceLib);
        Assert.NotNull(result.Pcm16);
        Assert.Contains("BRAKE", TestPaths.LoadVoiceLib().FindByText(result.Text)!.IntentTags);
    }

    [Fact]
    public void hint精确命中时优先使用该词条()
    {
        var router = new TtsRouter(TestPaths.LoadVoiceLib());

        var result = router.Route("voicelib://L001", "随便的文本", Intents.Ignore, Emotion.Calm);

        Assert.True(result.FromVoiceLib);
        Assert.Equal("好，左转……我看着点后视镜", result.Text);
    }

    [Fact]
    public void 既无语音库也无实时引擎时_退化为纯字幕()
    {
        var router = new TtsRouter(VoiceLib.Empty);

        var result = router.Route(null, "无音频", Intents.Ignore, Emotion.Calm);

        Assert.False(result.FromVoiceLib);
        Assert.Null(result.Pcm16);
        Assert.Equal("无音频", result.Text);
    }

    [Fact]
    public void 语音库为空时后端名为none()
    {
        Assert.Equal("none", new TtsRouter(VoiceLib.Empty).Name);
        Assert.Equal("voicelib", new TtsRouter(TestPaths.LoadVoiceLib()).Name);
    }
}