using System;
using System.Collections.Generic;

namespace Copilot.Core.Gameplay
{
    /// <summary>结算面板上的一行明细。</summary>
    public sealed class SettlementItem
    {
        public SettlementItem(string label, float value, int points)
        {
            Label = label ?? string.Empty;
            Value = value;
            Points = points;
        }

        public string Label { get; }

        /// <summary>原始计数 / 比值（显示用）。</summary>
        public float Value { get; }

        /// <summary>它对总分的贡献（正负都可）。</summary>
        public int Points { get; }
    }

    /// <summary>一局的结算报告。</summary>
    public sealed class SettlementReport
    {
        public SettlementReport(int safetyPoints, int interventionPoints, int tutorialPoints,
            int score, int stars, IReadOnlyList<SettlementItem> items)
        {
            SafetyPoints = safetyPoints;
            InterventionPoints = interventionPoints;
            TutorialPoints = tutorialPoints;
            Score = score;
            Stars = stars;
            Items = items ?? Array.Empty<SettlementItem>();
        }

        /// <summary>安全分（0..40）：撞车 / 压线扣分。</summary>
        public int SafetyPoints { get; }

        /// <summary>及时干预分（0..40）：救回来的比例。</summary>
        public int InterventionPoints { get; }

        /// <summary>教学完成分（0..20）：走完的教学步比例。</summary>
        public int TutorialPoints { get; }

        /// <summary>总分（0..100）。</summary>
        public int Score { get; }

        /// <summary>星级 0..3。</summary>
        public int Stars { get; }

        public IReadOnlyList<SettlementItem> Items { get; }
    }

    /// <summary>
    /// 一局（一个关卡）的累计台账与结算。纯逻辑，可单测。
    ///
    /// 三个维度分开计分，是因为它们回答的是三个不同的问题：
    /// **安全分** = 她有没有出事；**干预分** = 你这位教练有没有及时开口；
    /// **教学分** = 该教的动作有没有教完。混在一起会让"开得慢所以没撞"这种平庸局也拿高分。
    ///
    /// 计分公式（0..100，边界有单测钉死）：
    /// <code>
    /// 安全分 = clamp(40 − 12×撞车 − 6×压线, 0, 40)
    /// 干预分 = (救回+漏掉)==0 ? 40 : round(40 × 救回/(救回+漏掉))
    /// 教学分 = round(20 × 完成步数/总步数)
    /// 总分   = 三项之和
    /// 星级   = ≥90 → 3★ ；≥70 → 2★ ；≥50 → 1★ ；否则 0★
    /// </code>
    ///
    /// 注：一次失误失败会**同时**记一次"漏掉"（干预分）和一次真实事件（安全分），
    /// 这是刻意的 —— 她既没被你及时纠正，也确实把车开偏了。
    /// </summary>
    public sealed class LevelSession
    {
        public const int SafetyFull = 40;
        public const int InterventionFull = 40;
        public const int TutorialFull = 20;
        public const int CollisionPenalty = 12;
        public const int LaneDeparturePenalty = 6;

        readonly int _totalSteps;

        public LevelSession(string levelId, int totalSteps)
        {
            LevelId = levelId ?? string.Empty;
            _totalSteps = totalSteps < 0 ? 0 : totalSteps;
        }

        public string LevelId { get; }

        public int TotalSteps => _totalSteps;

        /// <summary>教学关已完成步数（只增不减）。</summary>
        public int StepsCompleted { get; private set; }

        /// <summary>及时救回来的失误次数。</summary>
        public int Saves { get; private set; }

        /// <summary>没救回来的失误次数。</summary>
        public int Misses { get; private set; }

        public int Collisions { get; private set; }

        public int LaneDepartures { get; private set; }

        /// <summary>本局最终信任值（由 Unity 每帧回填）。</summary>
        public float FinalTrust { get; private set; } = TrustState.InitialTrust;

        public void ObserveStepAdvance(int completedSteps)
        {
            if (completedSteps > StepsCompleted) StepsCompleted = completedSteps;
        }

        public void ObserveMistake(MistakeOutcome outcome)
        {
            if (outcome == null) return;
            if (outcome.Saved) Saves++;
            else Misses++;
        }

        /// <summary>记账一次真实发生的本地事件（撞车 / 压线…）。</summary>
        public void ObserveLocalEvent(string eventName)
        {
            if (string.IsNullOrEmpty(eventName)) return;
            if (string.Equals(eventName, "collision", StringComparison.OrdinalIgnoreCase)) Collisions++;
            else if (string.Equals(eventName, "lane_departure", StringComparison.OrdinalIgnoreCase)) LaneDepartures++;
        }

        public void SetTrust(float trust) => FinalTrust = TrustState.Clamp(trust);

        /// <summary>HUD 上的实时分数（与结算同一套公式，随时可调）。</summary>
        public int LiveScore() => Settle().Score;

        public SettlementReport Settle()
        {
            int safety = Clamp(SafetyFull - CollisionPenalty * Collisions - LaneDeparturePenalty * LaneDepartures,
                0, SafetyFull);

            int attempts = Saves + Misses;
            int intervention = attempts == 0
                ? InterventionFull
                : (int)Math.Round(InterventionFull * (double)Saves / attempts);

            int tutorial = _totalSteps == 0
                ? 0
                : (int)Math.Round(TutorialFull * (double)StepsCompleted / _totalSteps);

            int score = safety + intervention + tutorial;
            int stars = score >= 90 ? 3 : score >= 70 ? 2 : score >= 50 ? 1 : 0;

            var items = new List<SettlementItem>
            {
                new SettlementItem("撞车", Collisions, -CollisionPenalty * Collisions),
                new SettlementItem("压线", LaneDepartures, -LaneDeparturePenalty * LaneDepartures),
                new SettlementItem("及时干预", Saves, intervention),
                new SettlementItem("漏掉的失误", Misses, 0),
                new SettlementItem("教学步", StepsCompleted, tutorial),
                new SettlementItem("最终信任", FinalTrust, 0),
            };

            return new SettlementReport(safety, intervention, tutorial, score, stars, items);
        }

        static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
