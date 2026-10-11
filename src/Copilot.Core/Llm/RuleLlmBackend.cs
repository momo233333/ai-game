using System;
using System.Threading;
using System.Threading.Tasks;

namespace Copilot.Core.Llm
{
    /// <summary>
    /// 规则引擎后端：无模型依赖的确定性降级路径（方案 §5.2 系列降级）。
    /// 用关键词映射意图 + 固定台词，保证「指令先执行、台词后播放」的闭环语义不变。
    /// </summary>
    public sealed class RuleLlmBackend : ILlmBackend
    {
        public string Name => "rule";
        public bool IsAvailable => true;

        public Task<LlmOutput> GenerateAsync(LlmRequest request, CancellationToken ct = default)
        {
            return Task.FromResult(Parse(request?.PlayerText ?? string.Empty));
        }

        /// <summary>从玩家文本中抽取意图与动作参数（可单测的纯函数）。</summary>
        public static LlmOutput Parse(string text)
        {
            var t = text ?? string.Empty;

            // 1) 危险指令优先：一律 IGNORE + 扣信任 + 斥责
            if (Intents.IsDangerousCommand(t))
            {
                return new LlmOutput
                {
                    Action = new DriveAction { Intent = Intents.Ignore, Confidence = 1f },
                    Reply = "哥你疯啦？这种指令我不执行，出事了怎么办。",
                    TrustDelta = -5f,
                };
            }

            // 2) 倒车（负油门）。要放在「停车」之前：「倒车入库」里没有停车字样，但「倒库」更具体
            if (ContainsAny(t, "倒车", "倒库", "往后倒", "往后开", "倒一下"))
            {
                return Move(Intents.Reverse, "倒车……我、我倒车不太熟啊。", 0f);
            }

            // 3) 停车 / 靠边（要先于“减速”判断，避免“靠边停车”被当成 BRAKE）
            if (ContainsAny(t, "停车", "靠边", "路边停", "别开了"))
            {
                return Move(Intents.Park, "靠边停是吧，好。", 0f);
            }

            // 4) 变道 / 并线
            if (ContainsAny(t, "变道", "并线", "换道", "超车"))
            {
                float steer = ContainsAny(t, "左") ? -0.3f : 0.3f;
                return Move(Intents.LaneChange, "变道，看后视镜。", 0f, steeringOverride: steer);
            }

            // 5) 左转 / 右转
            if (ContainsAny(t, "左转", "左拐", "往左", "向左", "左打"))
            {
                return Move(Intents.TurnLeft, "行，左转交给我", 1f);
            }
            if (ContainsAny(t, "右转", "右拐", "往右", "向右", "右打"))
            {
                return Move(Intents.TurnRight, "右转收到，看我打灯", 1f);
            }

            // 6) 加速
            if (ContainsAny(t, "加速", "快点", "踩油门", "油门", "提速", "快一点", "轰"))
            {
                return Move(Intents.Accelerate, "油门我来，抓好了", 0f);
            }

            // 7) 刹车 / 减速
            if (ContainsAny(t, "刹车", "减速", "慢点", "慢一点", "停一下", "刹"))
            {
                return Move(Intents.Brake, "减速减速，别催", 0f);
            }

            // 7) 说不清 → 反问（技术缺陷转化为玩法，方案 §5.1）
            return new LlmOutput
            {
                Action = ActionLimits.Canonical(Intents.QuestionBack),
                Reply = "啊？你说啥？风太大没听清。",
                TrustDelta = 0f,
            };
        }

        /// <summary>动作参数统一取自 <see cref="ActionLimits.Canonical"/>，与真实 LLM 后端共用一张表。</summary>
        private static LlmOutput Move(string intent, string reply, float trustDelta, float? steeringOverride = null)
        {
            var action = ActionLimits.Canonical(intent);
            if (steeringOverride.HasValue) action.Steering = steeringOverride.Value;

            return new LlmOutput { Action = action, Reply = reply, TrustDelta = trustDelta };
        }

        private static bool ContainsAny(string text, params string[] keys)
        {
            for (int i = 0; i < keys.Length; i++)
            {
                if (text.IndexOf(keys[i], StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }
    }
}