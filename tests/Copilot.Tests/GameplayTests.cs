using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Copilot.Core;
using Copilot.Core.Gameplay;
using Copilot.Core.Levels;
using Xunit;

namespace Copilot.Tests;

/// <summary>剧本失误执行器的状态机（红灯/绿灯/边界都钉住）。</summary>
public class MistakeDirectorTests
{
    /// <summary>造一个可控的失误定义（不依赖 <see cref="MistakeCatalog"/> 的具体数值，边界才好钉）。</summary>
    static MistakeDefinition Def(
        float windowMs = 3000f,
        float laneOffsetFailM = float.PositiveInfinity,
        float tooCloseFailM = float.PositiveInfinity,
        float dwellMs = 300f,
        bool requiresLaneGeometry = true,
        float minSpeedKmh = 8f,
        params string[] fixIntents)
    {
        return new MistakeDefinition("t", "测试失误", "啊！")
        {
            Trigger = new MistakeTrigger { MinCompletedSteps = 0, MinSpeedKmh = minSpeedKmh },
            WindowMs = windowMs,
            WrongAction = new DriveAction { Intent = Intents.LaneChange, Steering = 0.35f, HoldMs = 1500 },
            FixIntents = fixIntents,
            LaneOffsetFailM = laneOffsetFailM,
            TooCloseFailM = tooCloseFailM,
            OutcomeDwellMs = dwellMs,
            RequiresLaneGeometry = requiresLaneGeometry,
            FailLocalEvent = "lane_departure",
        };
    }

    static MistakeContext Ctx(
        float speedKmh = 20f,
        int completedSteps = 9,
        float elapsedSeconds = 0f,
        float distanceM = 0f,
        float lateralOffsetM = 0f,
        bool laneGeometryValid = true,
        float hazardM = float.PositiveInfinity)
    {
        return new MistakeContext
        {
            SpeedKmh = speedKmh,
            CompletedSteps = completedSteps,
            ElapsedSeconds = elapsedSeconds,
            DistanceTravelledM = distanceM,
            LateralOffsetM = lateralOffsetM,
            LaneGeometryValid = laneGeometryValid,
            DistanceAheadToHazardM = hazardM,
        };
    }

    static MistakeDirector Director(params MistakeDefinition[] script) => new MistakeDirector(script);

    [Fact]
    public void 车速不够时不演()
    {
        var d = Director(Def(minSpeedKmh: 30f));

        var tick = d.Tick(100f, Ctx(speedKmh: 10f));

        Assert.False(tick.Opened);
        Assert.Equal(MistakePhase.Idle, d.Phase);
        Assert.Equal(0, d.FiredCount);
    }

    [Fact]
    public void 几何不成立时依赖车道的失误拒绝开演()
    {
        var d = Director(Def(requiresLaneGeometry: true));

        var tick = d.Tick(100f, Ctx(laneGeometryValid: false));

        Assert.False(tick.Opened);
        Assert.Equal(0, d.FiredCount);
    }

    [Fact]
    public void 不依赖车道的失误在几何不成立时照样能演()
    {
        var d = Director(Def(requiresLaneGeometry: false));

        var tick = d.Tick(100f, Ctx(laneGeometryValid: false));

        Assert.True(tick.Opened);
    }

    [Fact]
    public void 开演只报一次且只注入一次()
    {
        var d = Director(Def(windowMs: 5000f));

        var first = d.Tick(100f, Ctx());
        var second = d.Tick(100f, Ctx());
        var third = d.Tick(100f, Ctx());

        Assert.True(first.Opened);
        Assert.True(first.InjectNow);
        Assert.NotNull(first.InjectAction);
        Assert.Equal(Intents.LaneChange, first.InjectAction!.Intent);

        Assert.False(second.Opened);
        Assert.False(second.InjectNow);
        Assert.False(third.Opened);
        Assert.Equal(1, d.FiredCount);
    }

    [Fact]
    public void 窗口内说了算救的意图就是救回来了()
    {
        var d = Director(Def(fixIntents: Intents.LaneChange));
        var outcomes = new List<MistakeOutcome>();
        d.Resolved += outcomes.Add;

        d.Tick(100f, Ctx());
        d.Tick(200f, Ctx());              // 过去 300ms 才开口
        bool saved = d.ObservePlayerIntent(Intents.LaneChange);

        Assert.True(saved);
        Assert.Single(outcomes);
        Assert.Equal(MistakeOutcomeKind.Saved, outcomes[0].Kind);
        Assert.True(outcomes[0].Saved);
        Assert.Equal(string.Empty, outcomes[0].LocalEventName);   // 救回来了不该扣信任
        Assert.True(outcomes[0].ReactionMs > 0f);
        Assert.Equal(MistakePhase.Idle, d.Phase);
    }

    [Fact]
    public void 说了不相干的意图不算救()
    {
        var d = Director(Def(fixIntents: Intents.Brake));

        d.Tick(100f, Ctx());
        bool saved = d.ObservePlayerIntent(Intents.Accelerate);

        Assert.False(saved);
        Assert.Equal(MistakePhase.Warning, d.Phase);
    }

    [Fact]
    public void 窗口耗尽没开口就是失败并给出本地事件()
    {
        var d = Director(Def(windowMs: 1000f));
        var outcomes = new List<MistakeOutcome>();
        d.Resolved += outcomes.Add;

        d.Tick(100f, Ctx());
        d.Tick(600f, Ctx());
        Assert.Empty(outcomes);                     // 累计 700ms，还没到 1000ms

        d.Tick(500f, Ctx());                        // 累计 1200ms > 1000ms

        Assert.Single(outcomes);
        Assert.Equal(MistakeOutcomeKind.FailedByTimeout, outcomes[0].Kind);
        Assert.Equal("lane_departure", outcomes[0].LocalEventName);
        Assert.Equal(-1f, outcomes[0].ReactionMs, 3);
    }

    [Fact]
    public void 救晚了不算救_结案后动作无效()
    {
        var d = Director(Def(windowMs: 1000f, fixIntents: Intents.Brake));
        var outcomes = new List<MistakeOutcome>();
        d.Resolved += outcomes.Add;

        d.Tick(100f, Ctx());
        d.Tick(1000f, Ctx());                       // 超时 → 已判失败
        bool saved = d.ObservePlayerIntent(Intents.Brake);   // 迟到的刹车

        Assert.False(saved);
        Assert.Single(outcomes);
        Assert.Equal(MistakeOutcomeKind.FailedByTimeout, outcomes[0].Kind);
    }

    [Fact]
    public void 偏移越线并持续足够久就立刻判物理失败()
    {
        var d = Director(Def(windowMs: 9000f, laneOffsetFailM: 1.0f, dwellMs: 300f));

        // 第 1 拍是「开演」那一帧，防抖计时从下一拍才开始累加
        Assert.True(d.Tick(100f, Ctx(lateralOffsetM: 1.5f)).Opened);

        d.Tick(100f, Ctx(lateralOffsetM: 1.5f));    // 越线 100ms
        Assert.Equal(MistakePhase.Warning, d.Phase);

        d.Tick(100f, Ctx(lateralOffsetM: 1.5f));    // 200ms
        Assert.Equal(MistakePhase.Warning, d.Phase);

        d.Tick(100f, Ctx(lateralOffsetM: 1.5f));    // 300ms → 结案

        Assert.Equal(MistakePhase.Idle, d.Phase);
        Assert.NotNull(d.LastOutcome);
        Assert.Equal(MistakeOutcomeKind.FailedByPhysics, d.LastOutcome!.Kind);
    }

    [Fact]
    public void 抖动不过防抖时长不判()
    {
        var d = Director(Def(windowMs: 9000f, laneOffsetFailM: 1.0f, dwellMs: 300f));

        d.Tick(100f, Ctx(lateralOffsetM: 1.5f));    // 越线 100ms
        d.Tick(100f, Ctx(lateralOffsetM: 0f));      // 回到线内 → 计数清零
        d.Tick(200f, Ctx(lateralOffsetM: 1.5f));    // 再越线 200ms，仍不够 300ms

        Assert.Equal(MistakePhase.Warning, d.Phase);
        Assert.Null(d.LastOutcome);
    }

    [Fact]
    public void 跟车过近判据不必依赖车道几何()
    {
        var d = Director(Def(windowMs: 9000f, tooCloseFailM: 12f, dwellMs: 300f, requiresLaneGeometry: false));

        d.Tick(100f, Ctx(laneGeometryValid: false, hazardM: 20f));
        d.Tick(400f, Ctx(laneGeometryValid: false, hazardM: 8f));   // 连续过近 400ms

        Assert.Equal(MistakePhase.Idle, d.Phase);
        Assert.Equal(MistakeOutcomeKind.FailedByPhysics, d.LastOutcome!.Kind);
    }

    [Fact]
    public void 窗口开着时撞车用撞车事件结案()
    {
        var d = Director(new MistakeDefinition("t", "撞", "啊")
        {
            Trigger = new MistakeTrigger { MinCompletedSteps = 0, MinSpeedKmh = 0f },
            WindowMs = 3000f,
            FixIntents = new[] { Intents.Brake },
            FailLocalEvent = "hard_brake",
            CollisionLocalEvent = "collision",
            RequiresLaneGeometry = false,
        });

        d.Tick(100f, Ctx());

        Assert.True(d.ReportCollision());
        Assert.Equal("collision", d.LastOutcome!.LocalEventName);
        Assert.Equal(MistakeOutcomeKind.FailedByPhysics, d.LastOutcome.Kind);
    }

    [Fact]
    public void 窗口没开时撞车不由失误处理()
    {
        var d = Director(Def());

        Assert.False(d.ReportCollision());      // 交给调用方按「独立撞车」上报
        Assert.Null(d.LastOutcome);
    }

    [Fact]
    public void 结案后有冷却期不连发()
    {
        var first = Def(windowMs: 1000f, fixIntents: Intents.Brake);
        var second = Def(windowMs: 1000f, fixIntents: Intents.Brake);
        var d = new MistakeDirector(new[] { first, second }, cooldownSeconds: 8f);

        d.Tick(100f, Ctx());
        d.Tick(1000f, Ctx());                       // 第一个超时结案，进入冷却
        Assert.Equal(1, d.FiredCount);

        // 冷却期内即便条件满足，也不开演第二个
        for (int i = 0; i < 5; i++) Assert.False(d.Tick(500f, Ctx()).Opened);
        Assert.Equal(1, d.FiredCount);

        // 冷却结束后（8s）第二个才开演
        var tick = d.Tick(6000f, Ctx());
        Assert.True(tick.Opened);
        Assert.Equal(2, d.FiredCount);
    }

    [Fact]
    public void 严格按剧本顺序_队首没到条件就不演下一个()
    {
        var head = new MistakeDefinition("head", "头", "啊")
        {
            Trigger = new MistakeTrigger { MinCompletedSteps = 99, MinSpeedKmh = 0f },  // 永远不满足
            WindowMs = 1000f,
            RequiresLaneGeometry = false,
        };
        var tail = Def(requiresLaneGeometry: false);
        var d = new MistakeDirector(new[] { head, tail });

        for (int i = 0; i < 5; i++) Assert.False(d.Tick(100f, Ctx()).Opened);

        Assert.Equal(0, d.FiredCount);
    }

    [Fact]
    public void 触发条件的时间类条件是或关系()
    {
        var d = new MistakeDirector(new[]
        {
            new MistakeDefinition("t", "t", "啊")
            {
                Trigger = new MistakeTrigger { MinCompletedSteps = 3, MinElapsedSeconds = 10f, MinSpeedKmh = 5f },
                WindowMs = 1000f,
                RequiresLaneGeometry = false,
            },
        });

        // 步数不够但时间到了 → 照样触发
        var tick = d.Tick(100f, Ctx(completedSteps: 0, elapsedSeconds: 12f));
        Assert.True(tick.Opened);
    }

    [Fact]
    public void 没有触发条件时只看车速门槛()
    {
        var d = new MistakeDirector(new[]
        {
            new MistakeDefinition("t", "t", "啊")
            {
                Trigger = new MistakeTrigger { MinSpeedKmh = 5f },
                WindowMs = 1000f,
                RequiresLaneGeometry = false,
            },
        });

        Assert.False(d.Tick(100f, Ctx(speedKmh: 1f)).Opened);
        Assert.True(d.Tick(100f, Ctx(speedKmh: 10f)).Opened);
    }

    [Fact]
    public void 空剧本什么都不会发生()
    {
        var d = new MistakeDirector(new MistakeDefinition[0]);

        Assert.False(d.Tick(100f, Ctx()).Opened);
        Assert.True(d.IsScriptFinished);
    }

    [Fact]
    public void 重置后剧本能从头再演一遍()
    {
        var d = Director(Def(windowMs: 1000f));
        d.Tick(100f, Ctx());
        d.Tick(3000f, Ctx());                       // 超时结案（顺带进入 8s 冷却）
        Assert.Equal(1, d.FiredCount);

        d.Reset();

        Assert.Equal(0, d.FiredCount);
        Assert.Equal(MistakePhase.Idle, d.Phase);
        Assert.Null(d.LastOutcome);
        Assert.Null(d.Current);
        Assert.True(d.Tick(100f, Ctx()).Opened);    // 冷却与队首指针都清了 → 立刻能再演
    }
}

/// <summary>科目一剧本内容本身的合理性（防将来改数值改出事故）。</summary>
public class MistakeCatalogTests
{
    [Fact]
    public void 科目一有两个剧本失误且顺序是偏道在前()
    {
        Assert.Equal(2, MistakeCatalog.Subject1Tutorial.Count);
        Assert.Equal("drift_out_of_lane", MistakeCatalog.Subject1Tutorial[0].Id);
        Assert.Equal("forgot_to_brake", MistakeCatalog.Subject1Tutorial[1].Id);
    }

    [Fact]
    public void 每个失误的失败事件都必须在信任词表里()
    {
        // 防呆：拼错事件名会让信任静默不扣，且没有任何报错 —— 只有这条断言能拦住
        foreach (var def in MistakeCatalog.Subject1Tutorial)
        {
            Assert.True(TrustState.LocalEvents.ContainsKey(def.FailLocalEvent),
                $"{def.Id} 的 FailLocalEvent「{def.FailLocalEvent}」不在 TrustState.LocalEvents 里");

            if (!string.IsNullOrEmpty(def.CollisionLocalEvent))
            {
                Assert.True(TrustState.LocalEvents.ContainsKey(def.CollisionLocalEvent),
                    $"{def.Id} 的 CollisionLocalEvent「{def.CollisionLocalEvent}」不在 TrustState.LocalEvents 里");
            }
        }
    }

    [Fact]
    public void 每个失误都要有救回来的口令与开演台词()
    {
        foreach (var def in MistakeCatalog.Subject1Tutorial)
        {
            Assert.NotEmpty(def.FixIntents);
            Assert.False(string.IsNullOrWhiteSpace(def.Title));
            Assert.False(string.IsNullOrWhiteSpace(def.PanicLine));
            Assert.True(def.WindowMs >= 1500f, "干预窗口太短会让人来不及反应");
        }
    }

    [Fact]
    public void 算救的意图都是合法意图()
    {
        foreach (var def in MistakeCatalog.Subject1Tutorial)
        {
            foreach (var intent in def.FixIntents)
            {
                Assert.Contains(intent, Intents.All);
            }
            Assert.Contains(def.WrongAction.Intent, Intents.All);
        }
    }

    // ---- 语音提示：id 与字幕文本必须一致 ----
    // 这条防的是"看到的和听到的不是同一句"（H 阶段踩过）。语音库 id 一旦和文本脱节，
    // 游戏里就会出现"字幕写 A、喇叭念 B"，而且**没有任何报错**。

    [Fact]
    public void 每个语音提示的id与字幕文本必须一致()
    {
        var corpus = LoadCorpus();

        foreach (var def in MistakeCatalog.Subject1Tutorial)
        {
            AssertCue(def.Id + " 开演", def.Panic, corpus);
            AssertCue(def.Id + " 救回", def.Saved, corpus);
            AssertCue(def.Id + " 失手", def.Failed, corpus);
        }
    }

    [Fact]
    public void 玩法专用台词用GAMEPLAY标签_不会混进意图兜底池()
    {
        // 用真意图做标签的话，TtsRouter 的"按意图兜底"会在普通指令上念出她的慌乱台词
        var corpus = LoadCorpus();
        var gameplayIds = new List<string>();
        foreach (var def in MistakeCatalog.Subject1Tutorial)
        {
            gameplayIds.Add(def.Panic.Id);
            gameplayIds.Add(def.Saved.Id);
            gameplayIds.Add(def.Failed.Id);
        }

        foreach (var id in gameplayIds)
        {
            var line = ReadCorpusLine(id);
            Assert.NotNull(line);
            var tags = line!.Value.Tags;
            foreach (var intent in Intents.All)
            {
                Assert.DoesNotContain(intent, tags);   // 不能挂任何真意图
            }
        }
    }

    static void AssertCue(string who, VoiceCue cue, IReadOnlyDictionary<string, string> corpus)
    {
        Assert.True(cue.HasVoice, who + " 没配语音 id");
        Assert.True(corpus.ContainsKey(cue.Id), who + " 的 id「" + cue.Id + "」不在 lines.csv 里");
        Assert.Equal(corpus[cue.Id], cue.Text);        // ★ 听到的必须等于看到的
    }

    /// <summary>读 lines.csv 的 id → text（与 <c>Cli.ReadCorpus</c> 同一约定：台词里不含 ASCII 逗号）。</summary>
    static IReadOnlyDictionary<string, string> LoadCorpus()
    {
        var map = new Dictionary<string, string>();
        foreach (var line in File.ReadAllLines(CorpusPath, Encoding.UTF8))
        {
            if (line.Length == 0 || line.StartsWith("id,", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = line.Split(',');
            if (parts.Length < 2) continue;
            map[parts[0].Trim()] = parts[1].Trim();
        }
        Assert.NotEmpty(map);
        return map;
    }

    static (string Text, string[] Tags)? ReadCorpusLine(string id)
    {
        foreach (var line in File.ReadAllLines(CorpusPath, Encoding.UTF8))
        {
            var parts = line.Split(',');
            if (parts.Length < 4 || !string.Equals(parts[0].Trim(), id, StringComparison.Ordinal)) continue;
            var tags = parts[3].Trim().Split('|');
            return (parts[1].Trim(), tags);
        }
        return null;
    }

    static string CorpusPath => Path.Combine(TestPaths.RepoRoot, "voice-corpus", "lines.csv");
}

/// <summary>结算计分（三条指标 + 星级边界）。</summary>
public class LevelSessionTests
{
    static MistakeOutcome Saved(string id = "m")
        => new MistakeOutcome(id, MistakeOutcomeKind.Saved, string.Empty, 900f, 3000f);

    static MistakeOutcome Failed(string id = "m", string evt = "lane_departure")
        => new MistakeOutcome(id, MistakeOutcomeKind.FailedByTimeout, evt, -1f, 3000f);

    [Fact]
    public void 零失误满步就是满分三星()
    {
        var s = new LevelSession("subject1_tutorial", 6);
        s.ObserveStepAdvance(6);

        var r = s.Settle();

        Assert.Equal(40, r.SafetyPoints);
        Assert.Equal(40, r.InterventionPoints);
        Assert.Equal(20, r.TutorialPoints);
        Assert.Equal(100, r.Score);
        Assert.Equal(3, r.Stars);
    }

    [Fact]
    public void 九十分正好是三星()
    {
        var s = new LevelSession("subject1_tutorial", 6);
        s.ObserveStepAdvance(3);                 // 教学分 10

        var r = s.Settle();

        Assert.Equal(90, r.Score);
        Assert.Equal(3, r.Stars);
    }

    [Fact]
    public void 一次撞车一次压线的安全分()
    {
        var s = new LevelSession("subject1_tutorial", 6);
        s.ObserveLocalEvent("collision");
        s.ObserveLocalEvent("lane_departure");
        s.ObserveStepAdvance(6);

        var r = s.Settle();

        Assert.Equal(40 - 12 - 6, r.SafetyPoints);      // 22
        Assert.Equal(82, r.Score);
        Assert.Equal(2, r.Stars);
    }

    [Fact]
    public void 安全分不会扣成负数()
    {
        var s = new LevelSession("subject1_tutorial", 6);
        for (int i = 0; i < 5; i++) s.ObserveLocalEvent("collision");

        Assert.Equal(0, s.Settle().SafetyPoints);
    }

    [Fact]
    public void 一半救回来就是一半干预分()
    {
        var s = new LevelSession("subject1_tutorial", 6);
        s.ObserveMistake(Saved());
        s.ObserveMistake(Failed());
        s.ObserveStepAdvance(3);                          // 教学分 10

        var r = s.Settle();

        Assert.Equal(20, r.InterventionPoints);
        Assert.Equal(70, r.Score);
        Assert.Equal(2, r.Stars);
    }

    [Fact]
    public void 一次都没救回来时干预分为零_且五十分刚好一星()
    {
        var s = new LevelSession("subject1_tutorial", 6);
        s.ObserveMistake(Failed());
        s.ObserveStepAdvance(3);                          // 教学分 10

        var r = s.Settle();

        Assert.Equal(0, r.InterventionPoints);
        Assert.Equal(50, r.Score);
        Assert.Equal(1, r.Stars);
    }

    [Fact]
    public void 四十三分往下就没有星()
    {
        var s = new LevelSession("subject1_tutorial", 6);
        s.ObserveMistake(Failed());
        s.ObserveStepAdvance(1);                          // 教学分 3

        var r = s.Settle();

        Assert.Equal(43, r.Score);
        Assert.Equal(0, r.Stars);
    }

    [Fact]
    public void 一次失误失败同时记漏掉和真实事件()
    {
        // 这是刻意的：她既没被你及时纠正，也确实把车开偏了
        var s = new LevelSession("subject1_tutorial", 6);
        s.ObserveMistake(Failed());
        s.ObserveLocalEvent("lane_departure");

        Assert.Equal(1, s.Misses);
        Assert.Equal(1, s.LaneDepartures);
    }

    [Fact]
    public void 教学步只增不减()
    {
        var s = new LevelSession("subject1_tutorial", 6);
        s.ObserveStepAdvance(4);
        s.ObserveStepAdvance(2);

        Assert.Equal(4, s.StepsCompleted);
    }

    [Fact]
    public void 未知本地事件不污染计数()
    {
        var s = new LevelSession("subject1_tutorial", 6);
        s.ObserveLocalEvent("smooth_drive");
        s.ObserveLocalEvent("clean_pass");

        Assert.Equal(0, s.Collisions);
        Assert.Equal(0, s.LaneDepartures);
    }

    [Fact]
    public void 零步的关卡不会除零()
    {
        var s = new LevelSession("empty", 0);

        var r = s.Settle();

        Assert.Equal(0, r.TutorialPoints);
    }

    [Fact]
    public void 实时分数与结算分数同源()
    {
        var s = new LevelSession("subject1_tutorial", 6);
        s.ObserveStepAdvance(6);

        Assert.Equal(s.Settle().Score, s.LiveScore());
    }

    [Fact]
    public void 明细里能看到三项指标与信任()
    {
        var s = new LevelSession("subject1_tutorial", 6);
        s.SetTrust(52f);

        var r = s.Settle();

        Assert.Contains(r.Items, i => i.Label == "及时干预");
        Assert.Contains(r.Items, i => i.Label == "教学步");
        Assert.Contains(r.Items, i => i.Label == "最终信任" && i.Value == 52f);
    }

    [Fact]
    public void 重置后计数清零信任回初始值_关卡标识保留()
    {
        var s = new LevelSession("subject1_tutorial", 6);
        s.ObserveStepAdvance(5);
        s.ObserveMistake(Saved());
        s.ObserveMistake(Failed());
        s.ObserveLocalEvent("collision");
        s.ObserveLocalEvent("lane_departure");
        s.SetTrust(12f);

        s.Reset();

        Assert.Equal(0, s.StepsCompleted);
        Assert.Equal(0, s.Saves);
        Assert.Equal(0, s.Misses);
        Assert.Equal(0, s.Collisions);
        Assert.Equal(0, s.LaneDepartures);
        Assert.Equal(TrustState.InitialTrust, s.FinalTrust, 3);
        Assert.Equal("subject1_tutorial", s.LevelId);
        Assert.Equal(6, s.TotalSteps);
    }
}

/// <summary>重开一局：教学进度复位。</summary>
public class TutorialProgressResetTests
{
    [Fact]
    public void 重置后回到第一步()
    {
        var p = new TutorialProgress(LevelCatalog.Subject1Tutorial);
        for (int i = 0; i < 3; i++)
        {
            p.Observe(LevelCatalog.Subject1Tutorial.Steps[i].AcceptIntents[0]);
        }
        Assert.Equal(3, p.StepIndex);

        p.Reset();

        Assert.Equal(0, p.StepIndex);
        Assert.False(p.IsCompleted);
        Assert.Equal("起步", p.Current!.Title);
    }
}
