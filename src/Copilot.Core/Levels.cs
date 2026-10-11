using System;
using System.Collections.Generic;

namespace Copilot.Core.Levels
{
    /// <summary>教学关的一步：教练该说什么、学员做对了什么算过关。</summary>
    public sealed class TutorialStep
    {
        public TutorialStep(string title, string instruction, string examplePhrase, params string[] acceptIntents)
        {
            Title = title;
            Instruction = instruction;
            ExamplePhrase = examplePhrase;
            AcceptIntents = acceptIntents ?? Array.Empty<string>();
        }

        /// <summary>步骤名（HUD 显示，如「起步」）。</summary>
        public string Title { get; }

        /// <summary>给教练的说明（HUD 显示，如「让她慢慢给油，车动起来」）。</summary>
        public string Instruction { get; }

        /// <summary>示例口令（HUD 显示，玩家照着念即可）。</summary>
        public string ExamplePhrase { get; }

        /// <summary>学员做出这些动作中的任一个就算这一步过了。</summary>
        public IReadOnlyList<string> AcceptIntents { get; }
    }

    /// <summary>关卡定义。科目一到科目四共用这个结构。</summary>
    public sealed class LevelDefinition
    {
        public LevelDefinition(string id, string subject, string title, IReadOnlyList<TutorialStep> steps)
        {
            Id = id;
            Subject = subject;
            Title = title;
            Steps = steps ?? Array.Empty<TutorialStep>();
        }

        public string Id { get; }

        /// <summary>科目名（如「科目一」）。</summary>
        public string Subject { get; }

        public string Title { get; }
        public IReadOnlyList<TutorialStep> Steps { get; }
    }

    /// <summary>
    /// 《驾考宝典》关卡表。
    ///
    /// 目前只有科目一的教学关落地；科目二/三/四是**占位定义**，用来把结构定下来
    /// （场地五项 / 路考 / 安全文明），具体步骤等各自开工时再填。
    ///
    /// 类名刻意不叫 <c>Levels</c> —— 会和本文件所在的 <c>Copilot.Core.Levels</c> 命名空间同名，
    /// 引用时到处要写全限定名，很难看也容易写错。
    /// </summary>
    public static class LevelCatalog
    {
        /// <summary>
        /// 科目一 · 教学关（上车第一课）：六步走完基础操作，每步一个动作。
        /// 顺序刻意从「起步 → 转向 → 变道 → 减速 → 停车」，跟真实教练带新手的顺序一致。
        /// </summary>
        public static readonly LevelDefinition Subject1Tutorial = new LevelDefinition(
            id: "subject1_tutorial",
            subject: "科目一",
            title: "教学关 · 上车第一课",
            steps: new[]
            {
                new TutorialStep("起步", "先让她把车发动起来，慢慢给油。", "起步，慢慢来",
                    Intents.Accelerate),

                new TutorialStep("左转", "前面路口左转，让她提前打方向、看后视镜。", "前面路口左转",
                    Intents.TurnLeft),

                new TutorialStep("右转", "下一个路口右转，注意右后方。", "前面右转",
                    Intents.TurnRight),

                new TutorialStep("变道", "让她变到左边车道，先看镜子再动方向。", "向左变道",
                    Intents.LaneChange),

                new TutorialStep("减速", "让她把速度降下来。", "减速",
                    Intents.Brake),

                new TutorialStep("靠边停车", "最后靠边停稳，收工。", "靠边停车",
                    Intents.Park),
            });

        /// <summary>科目二 · 场地（倒车入库 / 侧方停车 / 曲线行驶…）—— 待设计。</summary>
        public static readonly LevelDefinition Subject2Field = new LevelDefinition(
            "subject2_field", "科目二", "场地驾驶 · 待设计", Array.Empty<TutorialStep>());

        /// <summary>科目三 · 路考（真实道路、路口、变道、靠边停车）—— 待设计。</summary>
        public static readonly LevelDefinition Subject3Road = new LevelDefinition(
            "subject3_road", "科目三", "道路驾驶 · 待设计", Array.Empty<TutorialStep>());

        /// <summary>科目四 · 安全文明驾驶（突发状况、危险指令拒绝）—— 待设计。</summary>
        public static readonly LevelDefinition Subject4Safety = new LevelDefinition(
            "subject4_safety", "科目四", "安全文明驾驶 · 待设计", Array.Empty<TutorialStep>());

        public static readonly IReadOnlyList<LevelDefinition> All = new[]
        {
            Subject1Tutorial, Subject2Field, Subject3Road, Subject4Safety,
        };
    }

    /// <summary>
    /// 教学关推进器：把「学员实际做出来的动作」翻译成「有没有过关」。
    ///
    /// 纯逻辑、无 Unity 依赖，可单测 —— 关卡规则属于游戏设计，不该埋在 MonoBehaviour 里。
    /// 规则很简单：**必须按当前这一步要求的动作来**，做别的（包括没听清的反问、被拒绝的 IGNORE）
    /// 一律不推进，也不倒退。
    /// </summary>
    public sealed class TutorialProgress
    {
        public TutorialProgress(LevelDefinition level)
        {
            Level = level ?? throw new ArgumentNullException(nameof(level));
        }

        public LevelDefinition Level { get; }

        public int StepIndex { get; private set; }

        public bool IsCompleted => StepIndex >= Level.Steps.Count;

        /// <summary>当前该做的步骤；全部完成时为 null。</summary>
        public TutorialStep? Current => IsCompleted ? null : Level.Steps[StepIndex];

        /// <summary>已完成步数 / 总步数（HUD 显示）。</summary>
        public int TotalSteps => Level.Steps.Count;

        /// <summary>
        /// 观察一个学员的实际动作。返回 true 表示这一步过掉了（HUD 该刷新）。
        /// </summary>
        public bool Observe(string? intent)
        {
            if (IsCompleted || string.IsNullOrEmpty(intent)) return false;

            var step = Level.Steps[StepIndex];
            for (int i = 0; i < step.AcceptIntents.Count; i++)
            {
                if (string.Equals(step.AcceptIntents[i], intent, StringComparison.Ordinal))
                {
                    StepIndex++;
                    return true;
                }
            }

            return false;
        }
    }
}