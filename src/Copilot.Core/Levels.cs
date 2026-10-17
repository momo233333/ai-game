using System;
using System.Collections.Generic;

namespace Copilot.Core.Levels
{
    /// <summary>教学关的一步：教练该说什么、学员做对了什么算过关、做错了怎么解释。</summary>
    public sealed class TutorialStep
    {
        public TutorialStep(string title, string instruction, string examplePhrase,
            string successCriteria, string whyWrong, int requiredDirection,
            params string[] acceptIntents)
        {
            Title = title;
            Instruction = instruction;
            ExamplePhrase = examplePhrase;
            SuccessCriteria = successCriteria;
            WhyWrong = whyWrong;
            RequiredDirection = requiredDirection;
            AcceptIntents = acceptIntents ?? Array.Empty<string>();
        }

        /// <summary>步骤名（HUD 显示，如「起步」）。</summary>
        public string Title { get; }

        /// <summary>给教练的说明（HUD 显示，如「让她慢慢给油，车动起来」）。</summary>
        public string Instruction { get; }

        /// <summary>示例口令（HUD 显示，玩家照着念即可）。</summary>
        public string ExamplePhrase { get; }

        /// <summary>**成功条件**（HUD 显示）：做到什么算这一步过了。</summary>
        public string SuccessCriteria { get; }

        /// <summary>**做错时说什么**（HUD 显示）：她做的不对，该提示玩家怎么纠正。</summary>
        public string WhyWrong { get; }

        /// <summary>
        /// 这一步要求的方向：**0 = 不校验**（如起步/减速），**−1 = 左，+1 = 右**。
        /// 只有「变道」这类**意图本身不带方向**的步骤才需要它 —— TURN_LEFT/TURN_RIGHT 的方向已写在意图里。
        /// </summary>
        public int RequiredDirection { get; }

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
        /// 科目一 · 教学关（训练场第一课）：**九步**走完基础操作。
        ///
        /// 顺序对齐「驾校练习场」的空间动线：先在练习道上起步/直行，再到弯区左转右转，
        /// 然后左右各变一次道，最后减速 → 倒车入库 → 靠边停车收工。
        /// 步骤拆细的动机（issue #2）：每个微任务都要有明确的目标、成功条件与做错解释。
        ///
        /// ⚠️ **没有引入任何新意图** —— 九步全部复用现有 9 个 intent，
        /// 所以不会波及 `Intents.All` → GBNF 文法 → PromptBuilder → 规则后端 → 41 条对话回归集。
        /// </summary>
        public static readonly LevelDefinition Subject1Tutorial = new LevelDefinition(
            id: "subject1_tutorial",
            subject: "科目一",
            title: "训练场 · 第一课",
            steps: new[]
            {
                new TutorialStep("起步", "先让她把车发动起来，慢慢给油。", "起步，慢慢来",
                    "车真的动起来", "她还没动呢 —— 说「起步，慢慢来」。",
                    0, Intents.Accelerate),

                new TutorialStep("直线保持", "让她沿着练习道直着往前，别乱打方向。", "直行，保持直线",
                    "车头保持朝前、继续往前开", "她要的是继续往前走 —— 说「直行」或「保持直线行驶」。",
                    0, Intents.Accelerate),

                new TutorialStep("左转", "到头有弯，让她提前打方向往左拐。", "前面左转",
                    "车头真的朝左偏（你说「左转」或「向左打一点」都算）",
                    "她没往左 —— 说「前面左转」或者「向左打一点方向」。",
                    -1, Intents.TurnLeft, Intents.LaneChange),

                new TutorialStep("右转", "折回来，让她往右拐。", "向右拐",
                    "车头真的朝右偏（你说「右转」或「向右打一点」都算）",
                    "她没往右 —— 说「向右拐」或者「向右打一点方向」。",
                    1, Intents.TurnRight, Intents.LaneChange),

                new TutorialStep("向左变道", "让她往左挪一条道。变道本身不带左右，方向靠你原话说清。", "向左变道",
                    "真的往左挪（转向为负）", "她挪反了方向 —— 变道时把「左」说清楚。",
                    -1, Intents.LaneChange, Intents.TurnLeft),

                new TutorialStep("向右变道", "这回往右挪一条道。", "向右变道",
                    "真的往右挪（转向为正）", "她挪反了方向 —— 说「向右变道」。",
                    1, Intents.LaneChange, Intents.TurnRight),

                new TutorialStep("减速", "到停止线前，让她把速度降下来。", "减速",
                    "刹车真的踩下去了", "她要的是减速 —— 说「减速」或「慢一点」。",
                    0, Intents.Brake),

                new TutorialStep("倒车", "开到库位前，让她慢慢往后倒。", "倒车，慢慢往后倒",
                    "车真的往后走（油门为负）", "她还没往后走 —— 说「倒车」或「往后倒一点」。",
                    0, Intents.Reverse),

                new TutorialStep("靠边停车", "最后靠边停稳，收工。", "靠边停车",
                    "车停稳", "她要的是停车 —— 说「靠边停车」。",
                    0, Intents.Park),
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

    /// <summary>一次动作观测的结果。</summary>
    public enum TutorialObservation
    {
        /// <summary>关卡已完成或没有动作 —— 什么也不做。</summary>
        NotApplicable,

        /// <summary>这一步过了，指针前进。</summary>
        Advanced,

        /// <summary>动作不是当前这一步要的（包括没听清的反问、被拒绝的 IGNORE）。</summary>
        WrongIntent,

        /// <summary>动作对了但**方向反了**（如该向左变道却往右）。</summary>
        WrongDirection,
    }

    /// <summary>
    /// 教学关推进器：把「学员实际做出来的动作」翻译成「有没有过关」。
    ///
    /// 纯逻辑、无 Unity 依赖，可单测 —— 关卡规则属于游戏设计，不该埋在 MonoBehaviour 里。
    /// 规则：**必须按当前这一步要求的动作来，做别的（包括没听清的反问、被拒绝的 IGNORE）一律不推进、不倒退。**
    ///
    /// 方向校验只在<b>步骤要求方向</b>时生效（目前只有「左/右转」「向左/向右变道」四处）。
    /// **方向不明时放行** —— 玩家可能只说「变道」而没说左右，不该卡住教学；
    /// 只有**明确报错方向**（车真的往反方向偏）才算 <see cref="TutorialObservation.WrongDirection"/>。
    ///
    /// ⚠️ **这四步的 AcceptIntents 是故意放宽的（实测反馈"明明往右拐了却通不过"）**：
    /// LLM 会把「向右打一点」「向右向右一直把方向打」这类**口语**归成 <c>LANE_CHANGE</c>
    /// （实测：`向右打一点 → ⇄变道 steering=+0.30`），而车**物理上确实往右拐了** ——
    /// 玩家眼里这就是"右转"，关卡却判「动作不对」，于是卡住。
    /// 所以转弯步也收 <c>LANE_CHANGE</c>、变道步也收对应的 <c>TURN_*</c>：
    /// **只把"往哪边"当硬约束，不纠结玩家把这叫转弯还是叫打方向。**
    /// 代价是「转弯」与「变道」两步在判据上区分度变低（只剩方向与顺序），
    /// 但这比"她把车拐过去了却不给过"要好 —— 教学关是练习场，别跟玩家的措辞较真。
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

        /// <summary>最近一次做错时该说的话（HUD 显示）；没做错时为 null。</summary>
        public string? LastWrongHint { get; private set; }

        /// <summary>重开一局：回到第一步。</summary>
        public void Reset()
        {
            StepIndex = 0;
            LastWrongHint = null;
        }

        /// <summary>
        /// 观察一个学员的实际动作。返回这一步发生了什么（HUD 据此刷新与提示）。
        /// </summary>
        /// <param name="intent">学员实际做出的意图。</param>
        /// <param name="direction">转向符号：−1 左 / +1 右 / 0 不明（见 <see cref="Intents.ParseDirection"/>）。</param>
        public TutorialObservation Observe(string? intent, int direction)
        {
            if (IsCompleted || string.IsNullOrEmpty(intent)) return TutorialObservation.NotApplicable;

            var step = Level.Steps[StepIndex];

            bool matched = false;
            for (int i = 0; i < step.AcceptIntents.Count; i++)
            {
                if (string.Equals(step.AcceptIntents[i], intent, StringComparison.Ordinal))
                {
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                LastWrongHint = step.WhyWrong;
                return TutorialObservation.WrongIntent;
            }

            // 方向不明（0）放行；只有明确反向才拦
            if (step.RequiredDirection != 0 && direction != 0 && direction != step.RequiredDirection)
            {
                LastWrongHint = step.WhyWrong;
                return TutorialObservation.WrongDirection;
            }

            StepIndex++;
            LastWrongHint = null;
            return TutorialObservation.Advanced;
        }
    }
}
