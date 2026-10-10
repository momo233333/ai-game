using Copilot.Core;
using Copilot.Core.Llm;
using Xunit;

namespace Copilot.Tests;

public class ActionLimitsTests
{
    [Fact]
    public void Clamp_把越界数值收回物理可行域()
    {
        var a = new DriveAction { Steering = 1.5f, Throttle = -0.3f, Brake = 9f, HoldMs = 99999, Confidence = 2f };

        var c = ActionLimits.Clamp(a);

        Assert.Equal(0.8f, c.Steering, 3);
        Assert.Equal(0f, c.Throttle, 3);
        Assert.Equal(1f, c.Brake, 3);
        Assert.Equal((int)ActionLimits.MaxHoldMs, c.HoldMs);
        Assert.Equal(1f, c.Confidence, 3);
    }

    [Fact]
    public void Clamp_不改动入参_返回副本()
    {
        var a = new DriveAction { Steering = 5f };
        ActionLimits.Clamp(a);
        Assert.Equal(5f, a.Steering, 3);
    }

    [Fact]
    public void Guard_白名单外意图降级为IGNORE并给出拒绝话术()
    {
        var g = ActionLimits.Guard(new DriveAction { Intent = "FLY_AWAY" }, new GameStateSnapshot());

        Assert.True(g.Intercepted);
        Assert.Equal(Intents.Ignore, g.Action.Intent);
        Assert.NotNull(g.ForcedReply);
    }

    [Fact]
    public void Guard_高速时停车指令被场景规则拒绝()
    {
        var state = new GameStateSnapshot { Telemetry = new VehicleTelemetry { SpeedKmh = 80f } };
        var g = ActionLimits.Guard(new DriveAction { Intent = Intents.Park }, state);

        Assert.True(g.Intercepted);
        Assert.Equal(Intents.Ignore, g.Action.Intent);
        Assert.Contains("80", g.ForcedReply);
    }

    [Fact]
    public void Guard_低速停车指令放行()
    {
        var state = new GameStateSnapshot { Telemetry = new VehicleTelemetry { SpeedKmh = 10f } };
        var g = ActionLimits.Guard(new DriveAction { Intent = Intents.Park, Brake = 0.6f }, state);

        Assert.False(g.Intercepted);
        Assert.Equal(Intents.Park, g.Action.Intent);
    }

    [Theory]
    [InlineData(Intents.TurnLeft)]
    [InlineData(Intents.TurnRight)]
    [InlineData(Intents.Accelerate)]
    [InlineData(Intents.Brake)]
    [InlineData(Intents.LaneChange)]
    [InlineData(Intents.Park)]
    [InlineData(Intents.Ignore)]
    [InlineData(Intents.QuestionBack)]
    public void 八种意图都在白名单内(string intent) => Assert.True(Intents.IsAllowed(intent));

    [Fact]
    public void 意图都有规范动作参数()
    {
        Assert.Equal(-0.6f, ActionLimits.Canonical(Intents.TurnLeft).Steering, 3);
        Assert.Equal(0.6f, ActionLimits.Canonical(Intents.TurnRight).Steering, 3);
        Assert.Equal(0.8f, ActionLimits.Canonical(Intents.Accelerate).Throttle, 3);
        Assert.Equal(0.8f, ActionLimits.Canonical(Intents.Brake).Brake, 3);
        Assert.Equal(0.6f, ActionLimits.Canonical(Intents.Park).Brake, 3);

        // 拒绝/反问不给动力
        foreach (var i in new[] { Intents.Ignore, Intents.QuestionBack })
        {
            var a = ActionLimits.Canonical(i);
            Assert.Equal(0f, a.Throttle, 3);
            Assert.Equal(0f, a.Brake, 3);
        }
    }

    /// <summary>
    /// 回归：实测 7B 模型只输出 intent、数值一律给 0，导致「车不动」。
    /// 约定 LLM 只决定做什么，参数由 Canonical 表补齐。
    /// </summary>
    [Fact]
    public void 回归_LLM只给intent时按规范表补齐参数()
    {
        var g = ActionLimits.Guard(new DriveAction { Intent = Intents.TurnLeft }, new GameStateSnapshot());

        Assert.Equal(Intents.TurnLeft, g.Action.Intent);
        Assert.Equal(-0.6f, g.Action.Steering, 3);
        Assert.Equal(0.3f, g.Action.Throttle, 3);
        Assert.Equal(1200, g.Action.HoldMs);
    }

    [Fact]
    public void 已给数值的动作按原值走限幅_不被规范表覆盖()
    {
        var a = new DriveAction
        {
            Intent = Intents.TurnLeft,
            Steering = -0.3f,
            Throttle = 0.5f,
            Brake = 0.01f,
            HoldMs = 800,
        };

        var g = ActionLimits.Guard(a, new GameStateSnapshot());

        Assert.Equal(-0.3f, g.Action.Steering, 3);
        Assert.Equal(0.5f, g.Action.Throttle, 3);
        Assert.Equal(800, g.Action.HoldMs);
    }
}

public class SafetyFilterTests
{
    private static SafetyFilter Filter() => new(new[] { "傻逼", "去死" });

    [Theory]
    [InlineData("你真傻逼")]
    [InlineData("傻 逼")]
    [InlineData("傻-逼")]
    [InlineData("傻，逼！")]
    public void 命中敏感词_含分隔符绕过(string text) => Assert.True(Filter().Contains(text));

    [Theory]
    [InlineData("前面路口左转")]
    [InlineData("")]
    [InlineData(null)]
    public void 正常文本不误伤(string? text) => Assert.False(Filter().Contains(text));

    [Fact]
    public void Sanitize_命中则替换为兜底台词()
    {
        var f = Filter();
        Assert.Equal("换一句", f.Sanitize("你去死吧", "换一句"));
        Assert.Equal("正常话", f.Sanitize("正常话", "换一句"));
    }
}

public class TrustStateTests
{
    [Fact]
    public void 初始信任为60且被夹在0到100之间()
    {
        var t = new TrustState();
        Assert.Equal(60f, t.Trust, 3);

        t.Apply(-1000f);
        Assert.Equal(0f, t.Trust, 3);

        t.Apply(1000f);
        Assert.Equal(100f, t.Trust, 3);
    }

    [Fact]
    public void 严重负面事件立即切到紧张()
    {
        var t = new TrustState(80f);
        t.Apply(-10f);
        Assert.Equal(Emotion.Nervous, t.Emotion);
    }

    [Theory]
    [InlineData(85f, 0f, Emotion.Happy)]
    [InlineData(55f, 0f, Emotion.Calm)]
    [InlineData(35f, 0f, Emotion.Nervous)]
    [InlineData(10f, 0f, Emotion.Annoyed)]
    public void 情绪档位按信任值分档(float trust, float delta, Emotion expected)
    {
        Assert.Equal(expected, TrustState.NextEmotion(trust, Emotion.Calm, delta));
    }

    [Fact]
    public void 本地事件表覆盖撞车与闯红灯()
    {
        var t = new TrustState();
        Assert.Equal(-12f, t.ApplyLocalEvent("collision"), 3);
        Assert.Equal(-8f, t.ApplyLocalEvent("red_light"), 3);
        Assert.Equal(0f, t.ApplyLocalEvent("unknown_event"), 3);
    }
}

public class RuleLlmBackendTests
{
    [Theory]
    [InlineData("前面路口左转", Intents.TurnLeft)]
    [InlineData("向左打方向", Intents.TurnLeft)]
    [InlineData("右拐一下", Intents.TurnRight)]
    [InlineData("踩油门快点", Intents.Accelerate)]
    [InlineData("刹车减速", Intents.Brake)]
    [InlineData("变道超车", Intents.LaneChange)]
    [InlineData("靠边停车", Intents.Park)]
    [InlineData("今天天气不错", Intents.QuestionBack)]
    public void 关键词映射到正确意图(string text, string expected)
    {
        Assert.Equal(expected, RuleLlmBackend.Parse(text).Action.Intent);
    }

    [Fact]
    public void 危险指令一律拒绝并扣信任()
    {
        var o = RuleLlmBackend.Parse("直接闯红灯冲过去");
        Assert.Equal(Intents.Ignore, o.Action.Intent);
        Assert.Equal(-5f, o.TrustDelta, 3);
    }

    /// <summary>
    /// 回归：曾把整段 Prompt 当玩家输入解析，Prompt 里的规则示例词（闯红灯/逆行）
    /// 导致所有指令都被误判为危险指令。
    /// </summary>
    [Fact]
    public void 回归_普通指令不会被规则示例词误判为危险()
    {
        var o = RuleLlmBackend.Parse("前面路口左转");
        Assert.Equal(Intents.TurnLeft, o.Action.Intent);
        Assert.NotEqual(-5f, o.TrustDelta);
    }

    [Fact]
    public void 停车判断优先于减速()
    {
        Assert.Equal(Intents.Park, RuleLlmBackend.Parse("靠边停车").Action.Intent);
    }
}

public class LlmJsonTests
{
    [Fact]
    public void 只取首个平衡对象_忽略模型后补的话()
    {
        const string raw = "好的 {\"action\":{\"intent\":\"BRAKE\"},\"reply\":\"踩了\"} 后面还有 {\"x\":1}";
        var o = LlmJson.Parse(raw);
        Assert.Equal(Intents.Brake, o.Action.Intent);
        Assert.Equal("踩了", o.Reply);
    }

    [Fact]
    public void 裸值自动补引号()
    {
        const string raw = "{\"action\":{\"intent\":\"TURN_LEFT\"},\"reply\":voicelib://L001,\"trust_delta\":1}";
        var o = LlmJson.Parse(raw);

        // 补引号后 JSON 才能解析成功（否则会落到兜底反问），URI 暂留在 reply，
        // 由 LlmOutputSanitizer 收拢到 tts_hint（见「输出归一化」用例）。
        Assert.Equal("voicelib://L001", o.Reply);
        Assert.Equal(Intents.TurnLeft, o.Action.Intent);
    }

    [Fact]
    public void 完整对象解析出全部字段()
    {
        const string raw = "{\"action\":{\"intent\":\"ACCELERATE\",\"steering\":0,\"throttle\":0.8,\"brake\":0,\"hold_ms\":1500,\"confidence\":0.9},\"tts_hint\":null,\"reply\":\"踩了\",\"trust_delta\":2}";
        var o = LlmJson.Parse(raw);

        Assert.Equal(Intents.Accelerate, o.Action.Intent);
        Assert.Equal(0.8f, o.Action.Throttle, 3);
        Assert.Equal(1500, o.Action.HoldMs);
        Assert.Equal("踩了", o.Reply);
        Assert.Null(o.TtsHint);
        Assert.Equal(2f, o.TrustDelta, 3);
    }

    [Fact]
    public void 完全不是JSON时兜底反问而不是抛异常()
    {
        var o = LlmJson.Parse("模型今天不想说话");
        Assert.Equal(Intents.QuestionBack, o.Action.Intent);
    }

    [Fact]
    public void 空输入兜底()
    {
        Assert.Equal(Intents.QuestionBack, LlmJson.Parse(null).Action.Intent);
    }

    // ---- 流式：action 对象一闭合就能提取（方案 §4.2 指令先执行）----

    [Fact]
    public void 流式_action未闭合时不提取()
    {
        Assert.False(LlmJson.TryExtractAction("{\"action\":{\"intent\":\"TURN_LEFT\"", out _));
        Assert.False(LlmJson.TryExtractAction("{\"reply\":\"还在想", out _));
        Assert.False(LlmJson.TryExtractAction(null, out _));
    }

    [Fact]
    public void 流式_action一闭合立即提取_此时reply还没生成()
    {
        const string partial = "{\"action\":{\"intent\":\"TURN_LEFT\",\"steering\":0,\"throttle\":0,\"brake\":0,\"hold_ms\":0,\"confidence\":0.9},\"reply\":\"左转";

        Assert.True(LlmJson.TryExtractAction(partial, out var action));
        Assert.Equal(Intents.TurnLeft, action.Intent);
    }

    [Fact]
    public void 流式_reply里的花括号不影响action提取()
    {
        const string partial = "{\"action\":{\"intent\":\"BRAKE\"},\"reply\":\"踩{刹车}了\"";

        Assert.True(LlmJson.TryExtractAction(partial, out var action));
        Assert.Equal(Intents.Brake, action.Intent);
    }

    [Fact]
    public void 输出归一化_把reply里的URI收拢到tts_hint()
    {
        var lib = TestPaths.LoadVoiceLib();
        var output = new LlmOutput { Reply = "voicelib://L001", TtsHint = null };

        var s = LlmOutputSanitizer.Sanitize(output, lib);

        Assert.Equal("voicelib://L001", s.TtsHint);
        Assert.NotEqual("voicelib://L001", s.Reply);
        Assert.Equal("行，左转交给我", s.Reply);
    }

    [Fact]
    public void 输出归一化_裸id补齐前缀()
    {
        var s = LlmOutputSanitizer.Sanitize(new LlmOutput { TtsHint = "L001" }, TestPaths.LoadVoiceLib());
        Assert.Equal("voicelib://L001", s.TtsHint);
    }
}