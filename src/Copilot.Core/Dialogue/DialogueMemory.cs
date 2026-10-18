using System;
using System.Collections.Generic;
using System.Text;

namespace Copilot.Core.Dialogue
{
    /// <summary>
    /// 对话记忆（IT方案1.md V2.0 §5.3）：滑动窗口 + 旧账队列。
    /// 摘要当前为规则式（不需要 LLM）；后续接入模型后可换成 LLM 异步压缩。
    /// </summary>
    public sealed class DialogueMemory
    {
        public const int DebtThreshold = -5;
        public const int MaxDebts = 5;

        private readonly int _windowSize;
        private readonly List<Turn> _turns = new List<Turn>();
        private readonly LinkedList<string> _debts = new LinkedList<string>();

        public DialogueMemory(int windowSize = 8)
        {
            _windowSize = windowSize < 1 ? 1 : windowSize;
        }

        public sealed class Turn
        {
            public string Player { get; set; } = string.Empty;
            public string Reply { get; set; } = string.Empty;
        }

        /// <summary>连续「跟开车无关」的回合数（结算见 <see cref="NoteIntent"/>）。</summary>
        public int OffTopicStreak { get; private set; }

        /// <summary>
        /// 容忍几轮跑题之后才开始提醒回到练车。**3 轮**：前两轮就陪她聊，第三轮起才收。
        /// 以前是「每轮都拉回」（用户反馈"太多强调回到开车中"），像客服而不是活人。
        /// </summary>
        public const int OffTopicTolerance = 3;

        /// <summary>
        /// 结算一回合的跑题状态：只有 <see cref="Intents.QuestionBack"/>（闲聊 / 追问 / 听不清）算跑题；
        /// 命中任何别的意图（驾驶动作，或危险拦截的 IGNORE —— 那也是驾驶相关）立刻归零。
        /// 计的是**连续**轮数：中间插一句驾驶指令，耐心就重新充满。
        /// </summary>
        public void NoteIntent(string? intent)
        {
            OffTopicStreak = string.Equals(intent, Intents.QuestionBack, StringComparison.Ordinal)
                ? OffTopicStreak + 1
                : 0;
        }

        public IReadOnlyList<Turn> RecentTurns => _turns;
        public IReadOnlyList<string> Debts
        {
            get
            {
                var list = new List<string>(_debts.Count);
                foreach (var d in _debts) list.Add(d);
                return list;
            }
        }

        public void AddTurn(string player, string reply)
        {
            _turns.Add(new Turn { Player = player ?? string.Empty, Reply = reply ?? string.Empty });
            while (_turns.Count > _windowSize) _turns.RemoveAt(0);
        }

        /// <summary>信任下降超过阈值时写入旧账（跨轮提醒，方案 §5.3）。</summary>
        public void NoteTrustDelta(float delta, string note)
        {
            if (delta > DebtThreshold) return;
            _debts.AddLast(note ?? string.Empty);
            while (_debts.Count > MaxDebts) _debts.RemoveFirst();
        }

        public string BuildContextBlock()
        {
            var sb = new StringBuilder();
            sb.Append("[记忆] ");
            if (_debts.Count == 0) sb.Append("（无旧账）");
            else sb.Append("旧账：").Append(string.Join("；", _debts));

            // 话题度：跑题**不立刻**拉回 —— 每轮变着法儿提「回去练车」最像机器人。
            // 攒够 OffTopicTolerance 轮才提醒一次。这段是每回合增量，不影响 KV 复用的静态前缀。
            //
            // ⚠️ 这一行**刻意写得又短又直白**（三轮实测换来的）：
            // ① 写成"解释型长句"（「教练连着 N 轮在聊跟开车无关的了 —— 这轮回复的**最后半句**自然地把话头带回练车
            //    （一句话说完、中间不要换行）…」60+ 字）→ **7B 从那一轮起直接退化**：输出连 JSON 的 `reply` 字段
            //    都写不全就被截断（9~12 token），容错解析器只好回落兜底台词「啊？你说啥？风太大没听清。」，
            //    玩家体感就是"她突然听不懂人话了"。
            // ② 只写关键词（`[话题] 该收一收了`）→ 不退化，但**多数时候收不回来**（模型不知道要干什么）。
            // ③ 短 + 明确指令（下面这句）→ 连续 3 轮都稳定地收尾「回去练车了教练～」，且输出完整。
            if (OffTopicStreak >= OffTopicTolerance)
            {
                sb.Append("\n[话题] 连着跑题 ").Append(OffTopicStreak).Append(" 轮了，末尾带一句回练车的话");
            }
            else if (OffTopicStreak > 0)
            {
                sb.Append("\n[话题] 现在还在闲聊（连第 ").Append(OffTopicStreak)
                  .Append(" 轮），**这轮不要提「回去练车」**，自然聊完就好");
            }

            return sb.ToString();
        }

        public void Clear()
        {
            _turns.Clear();
            _debts.Clear();
            OffTopicStreak = 0;
        }
    }
}