using System;
using System.Collections.Generic;

namespace Copilot.Core.Gameplay
{
    /// <summary>剧本失误的两个阶段：待机 / 已开演、等玩家干预。</summary>
    public enum MistakePhase
    {
        Idle,
        Warning,
    }

    /// <summary>一次失误的结果。</summary>
    public enum MistakeOutcomeKind
    {
        /// <summary>玩家在窗口内说了相关指令 —— 救回来了。</summary>
        Saved,

        /// <summary>窗口耗尽而玩家没开口（或说了不相干的）。</summary>
        FailedByTimeout,

        /// <summary>还没等玩家开口，物理上就已经出事了（真偏出车道 / 真撞上）。</summary>
        FailedByPhysics,
    }

    /// <summary>
    /// 一次失误的结算记录。Core 只负责判定，**扣信任的副作用交给
    /// <see cref="Copilot.Core.TurnPipeline.ApplyLocalEvent"/> / Unity 的
    /// <c>GameStateManager.ReportLocalEvent</c>** —— 这里只给出"该报哪个事件名"。
    /// </summary>
    public sealed class MistakeOutcome
    {
        public MistakeOutcome(string id, MistakeOutcomeKind kind, string localEventName,
            float reactionMs, float windowMs)
        {
            Id = id ?? string.Empty;
            Kind = kind;
            LocalEventName = localEventName ?? string.Empty;
            ReactionMs = reactionMs;
            WindowMs = windowMs;
        }

        public string Id { get; }
        public MistakeOutcomeKind Kind { get; }

        /// <summary>要上报的本地事件名（取 <see cref="TrustState.LocalEvents"/> 里的词）；救回来时为空。</summary>
        public string LocalEventName { get; }

        /// <summary>玩家从"开演"到开口干预的毫秒数；未救回时为 -1。</summary>
        public float ReactionMs { get; }

        public float WindowMs { get; }

        public bool Saved => Kind == MistakeOutcomeKind.Saved;
    }

    /// <summary>
    /// 每帧的观测样本：**Unity 负责测量，Core 负责判定**（延续「判定归代码」）。
    /// 它是 Unity 物理层与 Core 玩法规则之间的唯一数据桥。
    /// </summary>
    public sealed class MistakeContext
    {
        /// <summary>本局已过去的秒数。</summary>
        public float ElapsedSeconds { get; set; }

        /// <summary>教学关已完成步数（= <c>TutorialProgress.StepIndex</c>）。</summary>
        public int CompletedSteps { get; set; }

        public float DistanceTravelledM { get; set; }

        public float SpeedKmh { get; set; }

        /// <summary>离本车道中心的有符号横向偏移（右为正）；几何无效时应给 0。</summary>
        public float LateralOffsetM { get; set; }

        /// <summary>
        /// 横向偏移判据此刻是否成立。<c>false</c> = 车头没对正主路（转弯中）或在路口内，
        /// 此时「离 Z 向车道中心的偏移」没有意义 —— 依赖它的失误会**拒绝开演**而不是瞎判。
        /// </summary>
        public bool LaneGeometryValid { get; set; }

        /// <summary>沿车头方向到前方障碍的距离；没有障碍给 <c>float.PositiveInfinity</c>。</summary>
        public float DistanceAheadToHazardM { get; set; } = float.PositiveInfinity;
    }

    /// <summary>
    /// 失误的触发条件。**三个计时类条件是 OR 语义**（谁先满足算谁），
    /// <see cref="MinSpeedKmh"/> 是硬门槛。
    ///
    /// 为什么不只用「完成第 N 步」：玩家可能卡在某一步很久，那时失误永远不演，
    /// 玩法就演示不出来。加上时间/里程兜底后，**卡关时也照样能看到她在犯错**。
    /// </summary>
    public sealed class MistakeTrigger
    {
        /// <summary>完成步数达到它即可触发；&lt;0 = 不用这个条件。</summary>
        public int MinCompletedSteps { get; set; } = -1;

        /// <summary>本局经过的秒数达到它即可触发；&lt;0 = 不用这个条件。</summary>
        public float MinElapsedSeconds { get; set; } = -1f;

        /// <summary>行驶里程（米）达到它即可触发；&lt;0 = 不用这个条件。</summary>
        public float MinDistanceM { get; set; } = -1f;

        /// <summary>硬门槛：车速低于它一律不触发（停着不动时不该演失误）。</summary>
        public float MinSpeedKmh { get; set; }

        public bool IsSatisfied(MistakeContext ctx)
        {
            if (ctx == null) return false;
            if (ctx.SpeedKmh < MinSpeedKmh) return false;

            bool anyCondition = false;

            if (MinCompletedSteps >= 0)
            {
                anyCondition = true;
                if (ctx.CompletedSteps >= MinCompletedSteps) return true;
            }

            if (MinElapsedSeconds >= 0f)
            {
                anyCondition = true;
                if (ctx.ElapsedSeconds >= MinElapsedSeconds) return true;
            }

            if (MinDistanceM >= 0f)
            {
                anyCondition = true;
                if (ctx.DistanceTravelledM >= MinDistanceM) return true;
            }

            // 一个计时条件都没配 → 只看车速门槛（车速够了就演）
            return !anyCondition;
        }
    }

    /// <summary>
    /// 一个剧本失误：什么时候演、演什么错、玩家说什么算救回来、没救回会出什么事。
    /// </summary>
    public sealed class MistakeDefinition
    {
        public MistakeDefinition(string id, string title, string panicLine)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Title = title ?? string.Empty;
            PanicLine = panicLine ?? string.Empty;
        }

        /// <summary>唯一标识（日志与测试用）。</summary>
        public string Id { get; }

        /// <summary>HUD 警告横幅上的字，如「她偏出车道了！」。</summary>
        public string Title { get; }

        /// <summary>开演时小雅自己说的那句（走 DialogueBubble，不进 LLM）。</summary>
        public string PanicLine { get; }

        public MistakeTrigger Trigger { get; set; } = new MistakeTrigger();

        /// <summary>玩家有多少毫秒可以干预。</summary>
        public float WindowMs { get; set; } = 2800f;

        /// <summary>注入给车辆的动作（会走 <c>ActionLimits.Clamp</c> 与平滑爬升）。</summary>
        public DriveAction WrongAction { get; set; } = new DriveAction { Intent = Intents.Ignore };

        /// <summary>玩家产出这些意图中的任一个就算「及时干预」。空 = 永远救不回来。</summary>
        public IReadOnlyList<string> FixIntents { get; set; } = Array.Empty<string>();

        /// <summary>没救回来时上报的本地事件名（必须在 <see cref="TrustState.LocalEvents"/> 里）。</summary>
        public string FailLocalEvent { get; set; } = string.Empty;

        /// <summary>物理上真撞上时上报的事件名；默认与 <see cref="FailLocalEvent"/> 相同。</summary>
        public string CollisionLocalEvent { get; set; } = string.Empty;

        /// <summary>横向偏移超过它并持续 <see cref="OutcomeDwellMs"/> → 物理失败。∞ = 不用这个判据。</summary>
        public float LaneOffsetFailM { get; set; } = float.PositiveInfinity;

        /// <summary>前方距离小于它并持续 <see cref="OutcomeDwellMs"/> → 物理失败。∞ = 不用这个判据。</summary>
        public float TooCloseFailM { get; set; } = float.PositiveInfinity;

        /// <summary>防抖：物理判据要连续成立这么久才算数，避免单帧抖动误判。</summary>
        public float OutcomeDwellMs { get; set; } = 300f;

        /// <summary>
        /// 是否依赖车道几何。为 true 时，<see cref="MistakeContext.LaneGeometryValid"/> 为 false
        /// （转弯中 / 路口内）就**不开演** —— 宁可少演一次，也不要在几何不成立的坐标系里瞎判。
        /// </summary>
        public bool RequiresLaneGeometry { get; set; } = true;

        /// <summary>上报物理失败时用的事件名。</summary>
        public string ResolveFailEvent(bool byCollision)
            => byCollision && !string.IsNullOrEmpty(CollisionLocalEvent) ? CollisionLocalEvent : FailLocalEvent;
    }
}
