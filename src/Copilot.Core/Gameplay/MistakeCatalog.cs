using System.Collections.Generic;

namespace Copilot.Core.Gameplay
{
    /// <summary>
    /// 科目一的剧本失误表。与 <c>LevelCatalog</c> 同一约定：**游戏内容也放在 Core**
    /// （纯逻辑、可单测，不埋进 MonoBehaviour）。
    ///
    /// 参考作品《教妹妹开车》的张力全部来自「妹妹会自己犯错、你要抢在她出事前开口」——
    /// 这里就是那两个剧本失误。
    /// </summary>
    public static class MistakeCatalog
    {
        /// <summary>
        /// ① 偏出车道：她自顾自往右偏，玩家得让她回正。
        ///
        /// 注入动作是**中立的 <see cref="Intents.LaneChange"/> + 正转向**（往右），
        /// 所以「往回打方向」自然表达为 `LANE_CHANGE`（方向由玩家的原话决定，见
        /// <c>ActionLimits.ApplyDirection</c>）或 `TURN_LEFT`。按用户定的「**相关指令即可**」，
        /// 这里**不校验方向**（她往右偏时说往左、说变道都算救）。
        /// </summary>
        public static readonly MistakeDefinition DriftOutOfLane = new MistakeDefinition(
            id: "drift_out_of_lane",
            title: "她偏出车道了！快喊她回正",
            panicLine: "哎哎哎——我这方向盘怎么飘了？")
        {
            Trigger = new MistakeTrigger
            {
                // OR 语义：完成 2 步 **或** 25 秒后，都只差车速门槛
                MinCompletedSteps = 2,
                MinElapsedSeconds = 25f,
                MinSpeedKmh = 8f,
            },
            WindowMs = 2800f,
            WrongAction = new DriveAction
            {
                Intent = Intents.LaneChange,
                Steering = 0.35f,
                Throttle = 0.45f,
                HoldMs = 1500,
                Confidence = 0.9f,
            },
            FixIntents = new[] { Intents.LaneChange, Intents.TurnLeft },
            LaneOffsetFailM = 1.0f,
            FailLocalEvent = "lane_departure",
            RequiresLaneGeometry = true,

            // 语音提示（id 与文本必须一致，有单测钉死；Unity 拿不到语音库时只用文本）
            Panic = new VoiceCue("L072", "哎哎哎——我这方向盘怎么飘啦？"),
            Saved = new VoiceCue("L074", "呼……回正啦回正啦，吓我一跳呀"),
            Failed = new VoiceCue("L076", "教练你怎么不早说呀，我都飘出去啦"),
        };

        /// <summary>
        /// ② 忘了刹车：她一路给油冲向前车，玩家得喊刹车。
        ///
        /// 「前方那辆车」由 Unity 在**窗口打开那一刻**摆到车头正前方（见 <c>MistakeRunner</c>），
        /// 而不是常驻障碍 —— 常驻会在正常教学流程里伏击玩家（关卡从没让他们绕行）。
        ///
        /// 注：这一步在学习关卡第 5 步「减速」之前触发（第 5 步接受的正是
        /// <see cref="Intents.Brake"/>），所以**成功救回来会顺带把那一教学步过掉**。
        /// 这是特性不是 bug：她差点追尾、你喊了刹车，本来就该算你教会了她减速。
        /// </summary>
        public static readonly MistakeDefinition ForgotToBrake = new MistakeDefinition(
            id: "forgot_to_brake",
            title: "她要追上去了！快喊刹车",
            panicLine: "前面有车！我、我忘了踩刹车……")
        {
            Trigger = new MistakeTrigger
            {
                MinCompletedSteps = 3,
                MinElapsedSeconds = 45f,
                MinSpeedKmh = 10f,
            },
            WindowMs = 2600f,
            WrongAction = new DriveAction
            {
                Intent = Intents.Accelerate,
                Throttle = 0.8f,
                HoldMs = 2500,
                Confidence = 0.9f,
            },
            FixIntents = new[] { Intents.Brake },
            TooCloseFailM = 12f,
            FailLocalEvent = "hard_brake",
            CollisionLocalEvent = "collision",
            RequiresLaneGeometry = false,

            Panic = new VoiceCue("L073", "前面有车！我、我忘了踩刹车……"),
            Saved = new VoiceCue("L075", "刹住啦刹住啦，好险好险～"),
            Failed = new VoiceCue("L077", "我、我刚才是不是差点撞上呀……"),
        };

        /// <summary>科目一 · 教学关的剧本失误（严格按此顺序演）。</summary>
        public static readonly IReadOnlyList<MistakeDefinition> Subject1Tutorial = new[]
        {
            DriftOutOfLane,
            ForgotToBrake,
        };
    }
}
