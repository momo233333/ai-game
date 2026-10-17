using System.Linq;
using Copilot.Core;
using Copilot.Core.Levels;
using Copilot.Core.Llm;
using Xunit;

namespace Copilot.Tests;

public class ActionLimitsTests
{
    [Fact]
    public void Clamp_把越界数值收回物理可行域()
    {
        var a = new DriveAction { Steering = 1.5f, Throttle = -1.5f, Brake = 9f, HoldMs = 99999, Confidence = 2f };

        var c = ActionLimits.Clamp(a);

        Assert.Equal(0.8f, c.Steering, 3);
        Assert.Equal(-1f, c.Throttle, 3);   // 负油门 = 倒车，下限是 -1（不是 0）
        Assert.Equal(1f, c.Brake, 3);
        Assert.Equal((int)ActionLimits.MaxHoldMs, c.HoldMs);
        Assert.Equal(1f, c.Confidence, 3);
    }

    [Fact]
    public void Clamp_保留负油门_它是倒车不是错误值()
    {
        var c = ActionLimits.Clamp(new DriveAction { Throttle = -0.45f });

        Assert.Equal(-0.45f, c.Throttle, 3);
    }

    [Fact]
    public void Canonical_倒车给负油门()
    {
        var c = ActionLimits.Canonical(Intents.Reverse);

        Assert.Equal(Intents.Reverse, c.Intent);
        Assert.True(c.Throttle < 0f, "倒车必须给负油门");
    }

    // ---- 「往哪边」归代码（LANE_CHANGE 本身不带方向）----

    [Theory]
    [InlineData("向左变道", -1)]
    [InlineData("往右变道", 1)]
    [InlineData("左前方行驶", -1)]
    [InlineData("右前方行驶", 1)]
    [InlineData("变道", 0)]
    [InlineData("打方向盘", 0)]
    [InlineData("先往左再往右", 1)]
    // 玩家口语变体（实测反馈「说往左她往右」后补的一批，防止方向抽取漏词）
    [InlineData("往左开", -1)]
    [InlineData("靠左一点", -1)]
    [InlineData("左边转", -1)]
    [InlineData("往右边靠", 1)]
    [InlineData("右边拐", 1)]
    [InlineData("往右开", 1)]
    public void 从原话抽方向(string text, int expected)
    {
        Assert.Equal(expected, Intents.ParseDirection(text));
    }

    [Fact]
    public void 转向符号约定_负值是左_正值是右()
    {
        // 这条约定是全链路的底：Core 的 Canonical 给负数 = 左，Unity 的 WheelCollider.steerAngle 负数也 = 左。
        // 谁把符号翻过来，就会重新出现「说往左她往右」。
        Assert.True(ActionLimits.Canonical(Intents.TurnLeft).Steering < 0f, "TURN_LEFT 必须是负转向");
        Assert.True(ActionLimits.Canonical(Intents.TurnRight).Steering > 0f, "TURN_RIGHT 必须是正转向");
    }

    [Theory]
    [InlineData("向左变道", -1)]
    [InlineData("往左开", -1)]
    [InlineData("左前方行驶", -1)]
    [InlineData("靠左一点", -1)]
    [InlineData("向右变道", 1)]
    [InlineData("往右开", 1)]
    [InlineData("右前方行驶", 1)]
    [InlineData("往右边靠", 1)]
    public void 方向矫正_中立意图按原话给对左右(string text, int expectedSign)
    {
        // 回归：LANE_CHANGE 本身不带方向，Canonical 的默认值是 +0.3（右）。
        // 方向只能由代码从原话抽 —— 这条一旦断了，「向左变道」就会往右偏。
        var lane = ActionLimits.Guard(new DriveAction { Intent = Intents.LaneChange }, new GameStateSnapshot()).Action;

        ActionLimits.ApplyDirection(lane, Intents.ParseDirection(text));

        if (expectedSign < 0) Assert.True(lane.Steering < 0f, text + " 必须给负转向（左）");
        else Assert.True(lane.Steering > 0f, text + " 必须给正转向（右）");
    }

    [Fact]
    public void 方向矫正_向左变道给负转向()
    {
        // 回归：LLM 只输出 LANE_CHANGE，而 Canonical 把转向固定成 +0.3，
        // 于是「向左变道」车反而往右偏 —— 方向必须由代码从原话补。
        var lane = ActionLimits.Guard(new DriveAction { Intent = Intents.LaneChange }, new GameStateSnapshot()).Action;

        ActionLimits.ApplyDirection(lane, Intents.ParseDirection("向左变道"));

        Assert.True(lane.Steering < 0f, "向左变道必须给负转向（左）");
    }

    [Fact]
    public void 方向矫正_不动已经带方向的左右转()
    {
        var turn = ActionLimits.Canonical(Intents.TurnLeft);

        ActionLimits.ApplyDirection(turn, 1);

        Assert.True(turn.Steering < 0f, "TURN_LEFT 的方向已写在意图里，不该被覆盖");
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
        Assert.Equal("好，左转……我看着点后视镜", s.Reply);
    }

    [Fact]
    public void 输出归一化_裸id补齐前缀()
    {
        var s = LlmOutputSanitizer.Sanitize(new LlmOutput { TtsHint = "L001" }, TestPaths.LoadVoiceLib());
        Assert.Equal("voicelib://L001", s.TtsHint);
    }
}

// ---- GBNF 约束解码文法（OutputGrammar）----

public class OutputGrammarTests
{
    /// <summary>
    /// GBNF 里表示「JSON 字符串取值」的写法。
    /// GBNF 文本 <c>"\"TURN_LEFT\""</c> 匹配的是**带引号**的 <c>"TURN_LEFT"</c>；
    /// 外层的双引号只是 GBNF 字面量定界符，不算匹配内容。
    /// </summary>
    private static string Quoted(string value) => "\"\\\"" + value + "\\\"\"";

    [Fact]
    public void intent分支必须带引号_否则会生成裸标识符这种非法JSON()
    {
        // 回归：早先直接用 Lit 拼，GBNF 的 "TURN_LEFT" 只匹配裸值 TURN_LEFT，
        // 实测模型输出 {"action":{"intent": TURN_LEFT},...}（缺引号），不是合法 JSON。
        foreach (var intent in Intents.All)
        {
            Assert.Contains(Quoted(intent), OutputGrammar.Gbnf);
        }

        var expectedRule = "intent   ::= " + string.Join(" | ", Intents.All.Select(Quoted));
        Assert.Contains(expectedRule, OutputGrammar.Gbnf);
    }

    [Fact]
    public void 每个intent都在文法里_不会与代码漂移()
    {
        Assert.True(OutputGrammar.ContainsAllIntents());
    }

    [Fact]
    public void null是裸值_不带引号()
    {
        // JSON 的 null 本来就不带引号，别顺手也去加引号
        Assert.Contains("hint     ::= \"null\"", OutputGrammar.Gbnf);
    }
}

// ---- 关卡（《驾考宝典》）----

public class TutorialProgressTests
{
    [Fact]
    public void 按顺序做对动作_逐步推进直到完成()
    {
        var progress = new TutorialProgress(LevelCatalog.Subject1Tutorial);

        Assert.False(progress.IsCompleted);
        Assert.NotNull(progress.Current);

        foreach (var step in LevelCatalog.Subject1Tutorial.Steps)
        {
            Assert.False(progress.IsCompleted, $"「{step.Title}」还没做就不该完成");
            Assert.Equal(step.Title, progress.Current!.Title);
            Assert.Equal(TutorialObservation.Advanced,
                progress.Observe(step.AcceptIntents[0], step.RequiredDirection));
        }

        Assert.True(progress.IsCompleted);
        Assert.Null(progress.Current);
    }

    [Fact]
    public void 做错动作不推进也不倒退()
    {
        var progress = new TutorialProgress(LevelCatalog.Subject1Tutorial);
        var first = progress.Current!;

        // 第一步要的是起步（ACCELERATE），这里给左转、给拒绝、给反问都不该动
        Assert.Equal(TutorialObservation.WrongIntent, progress.Observe(Intents.TurnLeft, 0));
        Assert.Equal(TutorialObservation.WrongIntent, progress.Observe(Intents.Ignore, 0));
        Assert.Equal(TutorialObservation.WrongIntent, progress.Observe(Intents.QuestionBack, 0));
        Assert.Equal(TutorialObservation.NotApplicable, progress.Observe(null, 0));
        Assert.Equal(TutorialObservation.NotApplicable, progress.Observe(string.Empty, 0));

        Assert.Equal(0, progress.StepIndex);
        Assert.Equal(first.Title, progress.Current!.Title);
        Assert.NotNull(progress.LastWrongHint);   // 做错时必须给得出「为什么错」
    }

    [Fact]
    public void 完成后不再接受任何动作()
    {
        var progress = new TutorialProgress(LevelCatalog.Subject1Tutorial);
        foreach (var step in LevelCatalog.Subject1Tutorial.Steps)
        {
            progress.Observe(step.AcceptIntents[0], step.RequiredDirection);
        }

        Assert.True(progress.IsCompleted);
        Assert.Equal(TutorialObservation.NotApplicable, progress.Observe(Intents.Accelerate, 0));
        Assert.True(progress.IsCompleted);
    }

    [Fact]
    public void 变道方向反了不推进_方向不明则放行()
    {
        // 先过掉前四步，让当前步落到「向左变道」
        var progress = new TutorialProgress(LevelCatalog.Subject1Tutorial);
        for (int i = 0; i < 4; i++)
        {
            var s = LevelCatalog.Subject1Tutorial.Steps[i];
            progress.Observe(s.AcceptIntents[0], s.RequiredDirection);
        }

        var laneStep = progress.Current!;
        Assert.Equal("向左变道", laneStep.Title);
        Assert.Equal(-1, laneStep.RequiredDirection);

        // 意图对、方向反了 → 拦下，且不推进（这就是「说往左她往右」在关卡层的兜底）
        Assert.Equal(TutorialObservation.WrongDirection, progress.Observe(Intents.LaneChange, 1));
        Assert.Equal(laneStep.Title, progress.Current!.Title);
        Assert.NotNull(progress.LastWrongHint);

        // 方向不明（玩家只说「变道」）→ 放行，不该卡住教学
        Assert.Equal(TutorialObservation.Advanced, progress.Observe(Intents.LaneChange, 0));
        Assert.Equal("向右变道", progress.Current!.Title);
    }

    [Fact]
    public void 向右变道这一步_明确往左也会被拦下()
    {
        var progress = new TutorialProgress(LevelCatalog.Subject1Tutorial);
        for (int i = 0; i < 5; i++)
        {
            var s = LevelCatalog.Subject1Tutorial.Steps[i];
            progress.Observe(s.AcceptIntents[0], s.RequiredDirection);
        }

        Assert.Equal("向右变道", progress.Current!.Title);
        Assert.Equal(TutorialObservation.WrongDirection, progress.Observe(Intents.LaneChange, -1));
        Assert.Equal(TutorialObservation.Advanced, progress.Observe(Intents.LaneChange, 1));
    }

    [Fact]
    public void 重开后做错提示被清掉()
    {
        var progress = new TutorialProgress(LevelCatalog.Subject1Tutorial);
        progress.Observe(Intents.TurnLeft, 0);
        Assert.NotNull(progress.LastWrongHint);

        progress.Reset();

        Assert.Null(progress.LastWrongHint);
        Assert.Equal(0, progress.StepIndex);
    }

    [Fact]
    public void 教学关拆成九步_覆盖issue要求的练习序列()
    {
        var steps = LevelCatalog.Subject1Tutorial.Steps;
        var expected = new[]
        {
            "起步", "直线保持", "左转", "右转", "向左变道", "向右变道", "减速", "倒车", "靠边停车",
        };

        Assert.Equal(expected.Length, steps.Count);
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], steps[i].Title);
    }

    [Fact]
    public void 每一步都有示例口令与可接受动作_HUD才不会没话说()
    {
        foreach (var level in LevelCatalog.All)
        {
            foreach (var step in level.Steps)
            {
                Assert.False(string.IsNullOrWhiteSpace(step.Title));
                Assert.False(string.IsNullOrWhiteSpace(step.Instruction));
                Assert.False(string.IsNullOrWhiteSpace(step.ExamplePhrase));
                // issue #2 要求每一步都写清「成功条件」与「做错为什么」，否则 HUD 就没话可提示
                Assert.False(string.IsNullOrWhiteSpace(step.SuccessCriteria), $"「{step.Title}」缺成功条件");
                Assert.False(string.IsNullOrWhiteSpace(step.WhyWrong), $"「{step.Title}」缺做错解释");
                Assert.NotEmpty(step.AcceptIntents);
            }
        }
    }

    [Fact]
    public void 教学关之后还有科目二三四_四关结构定下来了()
    {
        Assert.Equal(4, LevelCatalog.All.Count);
        Assert.Equal("科目一", LevelCatalog.All[0].Subject);
        Assert.Equal("科目四", LevelCatalog.All[3].Subject);
        Assert.NotEmpty(LevelCatalog.Subject1Tutorial.Steps);
    }
}