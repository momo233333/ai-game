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
            return sb.ToString();
        }

        public void Clear()
        {
            _turns.Clear();
            _debts.Clear();
        }
    }
}