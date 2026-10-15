using System;
using System.Collections.Generic;

namespace Copilot.Core.Gameplay
{
    /// <summary>
    /// <see cref="MistakeDirector.Tick"/> 的返回值：这一帧要不要开演 / 要不要注入犯错动作。
    /// </summary>
    public readonly struct MistakeTick
    {
        public MistakeTick(bool opened, bool injectNow, DriveAction? injectAction)
        {
            Opened = opened;
            InjectNow = injectNow;
            InjectAction = injectAction;
        }

        /// <summary>本帧刚刚开演一个失误（HUD 该弹警告横幅了）。</summary>
        public bool Opened { get; }

        /// <summary>本帧要把 <see cref="InjectAction"/> 写进车辆 —— **只在开演那一帧为 true**。</summary>
        public bool InjectNow { get; }

        public DriveAction? InjectAction { get; }
    }

    /// <summary>
    /// 剧本失误的执行器（纯逻辑，可单测）。
    ///
    /// 责任边界：**Core 决定"什么时候演、玩家说什么算救回、没救回算哪个事件"**；
    /// Unity 负责测量（车速/偏移/距离/撞车）、把动作写进车辆、以及上报信任副作用。
    ///
    /// 判定规则（都有对应单测钉死）：
    /// 1. 触发 → 开演，同一帧只注入一次犯错动作；<c>HoldMs</c> 期间她会一直错下去。
    /// 2. 窗口内玩家产出 <see cref="MistakeDefinition.FixIntents"/> 里的任一个意图 → **Saved**（不产生本地事件）。
    /// 3. 物理判据（偏移 / 跟车过近）**连续成立 <c>OutcomeDwellMs</c>** → 立刻 **FailedByPhysics**（防抖）。
    /// 4. 窗口耗尽仍未干预 → **FailedByTimeout**，事件名取 <see cref="MistakeDefinition.FailLocalEvent"/>。
    /// 5. **救晚了不算救** —— 结案后不会被翻回 Saved。指标是「**及时**干预」。
    /// 6. 一次只跑一个失误，**严格按剧本顺序**（只看队首：队首没到条件就不跳过去演下一个）；
    ///    结案后冷却 <c>cooldownSeconds</c>，同一失误不重复触发。
    ///
    /// 解析只有一个出口：<see cref="Resolved"/> 事件（不另设返回值），避免调用方两处处理。
    /// </summary>
    public sealed class MistakeDirector
    {
        readonly IReadOnlyList<MistakeDefinition> _script;
        readonly float _cooldownSeconds;

        int _index;
        MistakePhase _phase = MistakePhase.Idle;
        MistakeDefinition? _current;
        float _remainingMs;
        float _windowTotalMs;
        float _dwellMs;
        float _cooldownMs;

        public MistakeDirector(IReadOnlyList<MistakeDefinition> script, float cooldownSeconds = 8f)
        {
            _script = script ?? throw new ArgumentNullException(nameof(script));
            _cooldownSeconds = cooldownSeconds;
        }

        /// <summary>剧本里总共几个失误。</summary>
        public int ScriptCount => _script.Count;

        /// <summary>已经开演过几个。</summary>
        public int FiredCount { get; private set; }

        /// <summary>全部演完（或都已结案）？</summary>
        public bool IsScriptFinished => _index >= _script.Count && _phase == MistakePhase.Idle;

        public MistakePhase Phase => _phase;

        /// <summary>当前正在等玩家干预的那个失误；Idle 时为 null。</summary>
        public MistakeDefinition? Current => _current;

        /// <summary>干预窗口总共多少毫秒。</summary>
        public float WindowMs => _windowTotalMs;

        /// <summary>窗口还剩多少毫秒（HUD 倒计时用）。</summary>
        public float RemainingMs => _remainingMs;

        public MistakeOutcome? LastOutcome { get; private set; }

        /// <summary>
        /// 结案时触发（Saved / FailedByTimeout / FailedByPhysics 都会走这里）。
        /// **Unity 侧只订阅这一个事件来处理结果**，不要同时去读 <see cref="MistakeTick"/>。
        /// </summary>
        public event Action<MistakeOutcome>? Resolved;

        /// <summary>每帧推进：触发 → 注入 → 判定（物理坏结果 / 窗口超时）。</summary>
        public MistakeTick Tick(float dtMs, MistakeContext ctx)
        {
            if (ctx == null || dtMs <= 0f) return default;

            if (_cooldownMs > 0f) _cooldownMs -= dtMs;

            if (_phase == MistakePhase.Warning && _current != null)
            {
                var def = _current;

                // 物理判据：真偏出去了 / 真贴上去了 —— 还没等玩家开口就已经出事
                bool breached = IsPhysicallyBreached(def, ctx);
                _dwellMs = breached ? _dwellMs + dtMs : 0f;
                if (breached && _dwellMs >= def.OutcomeDwellMs)
                {
                    Resolve(MistakeOutcomeKind.FailedByPhysics, byCollision: false);
                    return default;
                }

                _remainingMs -= dtMs;
                if (_remainingMs <= 0f) Resolve(MistakeOutcomeKind.FailedByTimeout, byCollision: false);

                return default;
            }

            // ---- Idle：找队首能不能开演 ----
            if (_cooldownMs > 0f || _index >= _script.Count) return default;

            var next = _script[_index];
            // 几何不成立（转弯中 / 路口内）时偏移判据无意义 → 这一条拒绝开演，但**不跳过**
            if (next.RequiresLaneGeometry && !ctx.LaneGeometryValid) return default;
            if (!next.Trigger.IsSatisfied(ctx)) return default;

            _current = next;
            _index++;
            _phase = MistakePhase.Warning;
            _windowTotalMs = next.WindowMs;
            _remainingMs = next.WindowMs;
            _dwellMs = 0f;
            FiredCount++;

            return new MistakeTick(opened: true, injectNow: true, injectAction: next.WrongAction);
        }

        /// <summary>
        /// 玩家做出了一个动作时调用（与 <c>TutorialProgress.Observe</c> 同风格）。
        /// true = 救回了当前失误。**调用方必须保证动作来自玩家**（Unity 侧从事件流取，
        /// 不要从 <c>ActionExecutor.ActionReceived</c> 取 —— 那个对注入的犯错动作也会触发）。
        /// </summary>
        public bool ObservePlayerIntent(string? intent)
        {
            if (_phase != MistakePhase.Warning || _current == null) return false;
            if (string.IsNullOrEmpty(intent)) return false;

            var fixes = _current.FixIntents;
            bool matched = false;
            for (int i = 0; i < fixes.Count; i++)
            {
                if (string.Equals(fixes[i], intent, StringComparison.Ordinal))
                {
                    matched = true;
                    break;
                }
            }

            if (!matched) return false;

            Resolve(MistakeOutcomeKind.Saved, byCollision: false);
            return true;
        }

        /// <summary>
        /// 真的撞上了。窗口开着 → 以物理失败立即结案（事件用 <c>CollisionLocalEvent</c>）并返回 true；
        /// 没开着 → 返回 false，由调用方按「独立撞车事件」自己上报。
        /// </summary>
        public bool ReportCollision()
        {
            if (_phase != MistakePhase.Warning || _current == null) return false;
            Resolve(MistakeOutcomeKind.FailedByPhysics, byCollision: true);
            return true;
        }

        /// <summary>
        /// 重开一局：清掉全部运行时状态，剧本从头再演一遍（<see cref="Resolved"/> 订阅者不受影响）。
        /// </summary>
        public void Reset()
        {
            _index = 0;
            _phase = MistakePhase.Idle;
            _current = null;
            _remainingMs = 0f;
            _windowTotalMs = 0f;
            _dwellMs = 0f;
            _cooldownMs = 0f;
            FiredCount = 0;
            LastOutcome = null;
        }

        bool IsPhysicallyBreached(MistakeDefinition def, MistakeContext ctx)
        {
            if (def.LaneOffsetFailM < float.PositiveInfinity
                && ctx.LaneGeometryValid
                && Math.Abs(ctx.LateralOffsetM) >= def.LaneOffsetFailM)
            {
                return true;
            }

            if (def.TooCloseFailM < float.PositiveInfinity
                && ctx.DistanceAheadToHazardM <= def.TooCloseFailM)
            {
                return true;
            }

            return false;
        }

        void Resolve(MistakeOutcomeKind kind, bool byCollision)
        {
            var def = _current;
            if (def == null) return;

            bool saved = kind == MistakeOutcomeKind.Saved;
            float reactionMs = saved ? _windowTotalMs - _remainingMs : -1f;
            string eventName = saved ? string.Empty : def.ResolveFailEvent(byCollision);

            var outcome = new MistakeOutcome(def.Id, kind, eventName, reactionMs, _windowTotalMs);
            LastOutcome = outcome;

            _current = null;
            _phase = MistakePhase.Idle;
            _remainingMs = 0f;
            _windowTotalMs = 0f;
            _dwellMs = 0f;
            _cooldownMs = _cooldownSeconds * 1000f;

            Resolved?.Invoke(outcome);
        }
    }
}
