using System;
using System.Collections.Generic;

namespace Copilot.Core
{
    /// <summary>四档情绪（与 proto.Emo​tion 对齐）。</summary>
    public enum Emotion
    {
        Calm = 0,
        Annoyed = 1,
        Nervous = 2,
        Happy = 3,
    }

    public static class EmotionNames
    {
        public static string ToWire(Emotion e)
        {
            switch (e)
            {
                case Emotion.Annoyed: return "ANNOYED";
                case Emotion.Nervous: return "NERVOUS";
                case Emotion.Happy: return "HAPPY";
                default: return "CALM";
            }
        }

        public static bool TryParse(string? text, out Emotion emotion)
        {
            emotion = Emotion.Calm;
            if (string.IsNullOrWhiteSpace(text)) return false;
            switch (text!.Trim().ToUpperInvariant())
            {
                case "CALM": emotion = Emotion.Calm; return true;
                case "ANNOYED": emotion = Emotion.Annoyed; return true;
                case "NERVOUS": emotion = Emotion.Nervous; return true;
                case "HAPPY": emotion = Emotion.Happy; return true;
                default: return false;
            }
        }
    }

    /// <summary>驾驶意图白名单（字符串常量与 proto 的 intent 字段一致）。</summary>
    public static class Intents
    {
        public const string TurnLeft = "TURN_LEFT";
        public const string TurnRight = "TURN_RIGHT";
        public const string Accelerate = "ACCELERATE";
        public const string Brake = "BRAKE";
        public const string LaneChange = "LANE_CHANGE";
        public const string Park = "PARK";
        public const string Ignore = "IGNORE";
        public const string QuestionBack = "QUESTION_BACK";

        /// <summary>
        /// 倒车。**动作参数用负油门表示**（<see cref="DriveAction.Throttle"/> &lt; 0），
        /// 这样不用为「倒挡」单独加字段、也不用改协议。
        /// </summary>
        public const string Reverse = "REVERSE";

        /// <summary>LLM 允许输出的意图全集（白名单）。</summary>
        public static readonly IReadOnlyList<string> All = new[]
        {
            TurnLeft, TurnRight, Accelerate, Brake, LaneChange, Park, Ignore, QuestionBack, Reverse,
        };

        public static bool IsAllowed(string? intent)
        {
            if (string.IsNullOrEmpty(intent)) return false;
            for (int i = 0; i < All.Count; i++)
            {
                if (string.Equals(All[i], intent, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// 危险驾驶指令关键词。**危险判定归代码，不归 LLM**：
        /// 实测 7B 会把「今天天气不错」「谢了」「帮我买个菜」都判成 IGNORE 并每次扣 5 点信任，
        /// 十几轮下来信任直接崩掉。危险指令有明显特征词，交给规则判最稳。
        /// </summary>
        public static readonly IReadOnlyList<string> DangerousKeywords = new[]
        {
            "闯红灯", "冲过去", "撞人", "撞他", "逆行", "喝酒", "酒驾", "飙车", "超速", "别停",
        };

        /// <summary>玩家这句话是不是危险驾驶指令（只有它才允许 IGNORE + 扣信任）。</summary>
        public static bool IsDangerousCommand(string? text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < DangerousKeywords.Count; i++)
            {
                if (text!.IndexOf(DangerousKeywords[i], StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        static readonly string[] LeftWords = { "左", "左拐", "左打", "左前", "往左", "向左", "左边" };
        static readonly string[] RightWords = { "右", "右拐", "右打", "右前", "往右", "向右", "右边" };

        /// <summary>
        /// 速度单位词 → km/h 换算系数。取**最后出现**的那一处。
        /// - 口语里「码」当 km/h 用（大陆的说法），「迈」是英里（1 迈 ≈ 1.609km/h）——
        ///   这两个不换算的话，说「开 5 迈」会被当成 5km/h，差 60%。
        /// - 刻意**不收「米」**：那是距离不是速度。
        /// </summary>
        static readonly (string Unit, float Factor)[] SpeedUnits =
        {
            ("km/h", 1f), ("km／h", 1f), ("kmh", 1f), ("km", 1f),
            ("千米每小时", 1f), ("公里每小时", 1f), ("千米", 1f), ("公里", 1f),
            ("码", 1f), ("迈", 1.609f),
        };

        /// <summary>
        /// 「慢一点 / 开慢点 / 别开快」这类**没给数字的降速要求**（由 Unity 侧按当前车速折算目标）。
        /// 刻意**不含「减速」「降速」** —— 那两个已经是 <c>BRAKE</c> 意图的关键词，收进来会两边打架。
        /// </summary>
        static readonly string[] SlowWords =
        {
            "慢一点", "慢点", "开慢", "慢些", "慢一些", "别开快", "别太快", "不要太快",
        };

        /// <summary>
        /// 从教练原话里抽**目标速度**（km/h）：0 = 没说。
        ///
        /// 和「方向判定归代码」同一个思路：LLM 只决定**做什么**（intent），
        /// **具体多快**由代码从原话里抽 —— 因为意图集里没有任何一个能承接「5km/h」这个数，
        /// 让 7B 去理解数值只会得到反问。抽到的值由 Unity 侧闭环控速（见 VehicleController）。
        ///
        /// ⚠️ **必须认中文数字**：ASR（paraformer）输出的就是汉字 —— 实测用户说的
        /// 「你把速度调到五公里每小时」整句都是汉字，一个阿拉伯数字都没有。只认 `0-9` 等于定速从未触发过。
        ///
        /// 规则：找到**最后出现的**速度单位词，再往左回扫数字（先阿拉伯、再中文）。
        /// 所以「用 5km/h 的速度行驶」→ 5、「开到 20 公里」→ 20、「前面 5 米」→ 0（不是速度词）。
        /// 已知过度匹配：「还有五公里就到了」这种**说距离**的话也会抽成 5 —— 游戏里教练说距离很少见，先接受。
        /// </summary>
        public static int ParseTargetSpeedKmh(string? text)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            int bestValue = 0;
            float bestFactor = 1f;
            int bestIndex = -1;

            for (int u = 0; u < SpeedUnits.Length; u++)
            {
                int idx = text!.LastIndexOf(SpeedUnits[u].Unit, StringComparison.OrdinalIgnoreCase);
                if (idx <= bestIndex) continue;   // 同位置时先出现的单位优先（"km/h" 先于 "km"）

                int value = NumberBefore(text!, idx);
                if (value <= 0) continue;

                bestValue = value;
                bestFactor = SpeedUnits[u].Factor;
                bestIndex = idx;
            }

            if (bestValue <= 0) return 0;

            int kmh = (int)Math.Round(bestValue * bestFactor);
            return kmh < 1 ? 1 : kmh > 120 ? 120 : kmh;
        }

        /// <summary>是不是「慢一点」这类**没给数字**的降速要求（由 Unity 侧按当前车速折算目标）。</summary>
        public static bool IsSlowRequest(string? text)
            => ContainsAny(text, SlowWords);

        private static bool ContainsAny(string? text, string[] keys)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < keys.Length; i++)
            {
                if (text!.IndexOf(keys[i], StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        /// <summary>单位词左边那个数：先试阿拉伯数字，再试中文数字（「五」「二十」「一百二十」）。</summary>
        private static int NumberBefore(string text, int endExclusive)
        {
            int i = endExclusive - 1;
            while (i >= 0 && (text[i] == ' ' || text[i] == '　' || text[i] == '、')) i--;
            if (i < 0) return 0;

            if (text[i] >= '0' && text[i] <= '9') return AsciiNumberBefore(text, i);
            return ChineseNumberBefore(text, i);
        }

        /// <summary>从 <paramref name="endInclusive"/> 往左扫连续阿拉伯数字（最多三位）。</summary>
        private static int AsciiNumberBefore(string text, int endInclusive)
        {
            int i = endInclusive;
            while (i >= 0 && text[i] >= '0' && text[i] <= '9') i--;
            int start = i + 1;

            if (endInclusive - start + 1 > 3) return 0;   // 超过三位不是车速（多半是别的数字）

            int value = 0;
            for (int k = start; k <= endInclusive; k++) value = value * 10 + (text[k] - '0');
            return value;
        }

        /// <summary>从 <paramref name="endInclusive"/> 往左扫连续中文数字（最多四位，够到「一百二十」）。</summary>
        private static int ChineseNumberBefore(string text, int endInclusive)
        {
            int i = endInclusive;
            while (i >= 0 && IsChineseNumeral(text[i]) && endInclusive - i < 4) i--;
            int start = i + 1;

            if (start > endInclusive) return 0;
            return ChineseToInt(text, start, endInclusive);
        }

        private static bool IsChineseNumeral(char c)
            => "零一二三四五六七八九十百两".IndexOf(c) >= 0;

        /// <summary>中文数字 → int（支持「五 / 十 / 十五 / 二十 / 一百二十」这类 0..999 的写法）。</summary>
        private static int ChineseToInt(string text, int start, int end)
        {
            int section = 0;
            int digit = 0;

            for (int k = start; k <= end; k++)
            {
                switch (text[k])
                {
                    case '零': digit = 0; break;
                    case '一': digit = 1; break;
                    case '二': case '两': digit = 2; break;
                    case '三': digit = 3; break;
                    case '四': digit = 4; break;
                    case '五': digit = 5; break;
                    case '六': digit = 6; break;
                    case '七': digit = 7; break;
                    case '八': digit = 8; break;
                    case '九': digit = 9; break;
                    case '十': section += (digit == 0 ? 1 : digit) * 10; digit = 0; break;
                    case '百': section += (digit == 0 ? 1 : digit) * 100; digit = 0; break;
                    default: return 0;
                }
            }

            return section + digit;
        }

        /// <summary>
        /// 从教练原话里抽方向：**-1 = 左，+1 = 右，0 = 没说**。
        ///
        /// 「方向判定归代码」和危险判定同一个思路：LLM 只决定**做什么**（intent），
        /// **往哪边**由代码从原话里抽 —— 因为 LANE_CHANGE 这类意图本身不带方向。
        /// 实测踩过的坑：LLM 只输出 `LANE_CHANGE`，而 `Canonical` 把转向固定成 +0.3，
        /// 于是「向左变道」车反而往右偏。
        ///
        /// 左右都出现时（「左右打方向」）取**最后出现**的那个。
        /// </summary>
        public static int ParseDirection(string? text)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            int left = LastIndexOfAny(text!, LeftWords);
            int right = LastIndexOfAny(text!, RightWords);
            if (left < 0 && right < 0) return 0;
            if (left < 0) return 1;
            if (right < 0) return -1;
            return right > left ? 1 : -1;
        }

        private static int LastIndexOfAny(string text, string[] words)
        {
            int last = -1;
            for (int i = 0; i < words.Length; i++)
            {
                int idx = text.LastIndexOf(words[i], StringComparison.Ordinal);
                if (idx > last) last = idx;
            }
            return last;
        }
    }

    /// <summary>结构化驾驶指令（proto DriveAction 的领域模型）。</summary>
    public sealed class DriveAction
    {
        public float Steering { get; set; }
        public float Throttle { get; set; }
        public float Brake { get; set; }
        public int HoldMs { get; set; }
        public string Intent { get; set; } = Intents.Ignore;
        public float Confidence { get; set; }

        public DriveAction Clone() => new DriveAction
        {
            Steering = Steering,
            Throttle = Throttle,
            Brake = Brake,
            HoldMs = HoldMs,
            Intent = Intent,
            Confidence = Confidence,
        };
    }

    public sealed class VehicleTelemetry
    {
        public float SpeedKmh { get; set; }
        public int Gear { get; set; }
        public bool AtIntersection { get; set; }
        public string ObstacleSummary { get; set; } = string.Empty;
    }

    public sealed class SceneContext
    {
        public string SceneId { get; set; } = string.Empty;
        public string Weather { get; set; } = string.Empty;
        public string TimeOfDay { get; set; } = string.Empty;
    }

    /// <summary>回推给 AI 的完整游戏状态（proto GameState 的领域模型）。</summary>
    public sealed class GameStateSnapshot
    {
        public float Trust { get; set; } = TrustState.InitialTrust;
        public Emotion Emotion { get; set; } = Emotion.Calm;
        public SceneContext Scene { get; set; } = new SceneContext();
        public VehicleTelemetry Telemetry { get; set; } = new VehicleTelemetry();
        public string LevelId { get; set; } = string.Empty;
    }

    public sealed class StateDelta
    {
        public float Trust { get; set; }
        public Emotion Emotion { get; set; }
        public float TrustDelta { get; set; }
    }

    public enum TurnEventKind
    {
        AsrPartial,
        AsrFinal,
        LlmDelta,
        TtsAudio,
        DriveAction,
        StateDelta,
        Error,
    }

    /// <summary>
    /// 下行事件。字段按 kind 复用，避免为每种事件建独立类型：
    /// 简单、可测、易映射到 proto oneof。
    /// </summary>
    public sealed class TurnEvent
    {
        public TurnEventKind Kind { get; private set; }

        /// <summary>ASR / LLM 文本。</summary>
        public string? Text { get; private set; }
        public float Confidence { get; private set; }
        public bool LowConfidence { get; private set; }

        public DriveAction? Action { get; private set; }
        public StateDelta? State { get; private set; }

        public byte[]? Pcm16 { get; private set; }
        public int SampleRate { get; private set; }
        public bool FromVoiceLib { get; private set; }

        public string? ErrorCode { get; private set; }
        public string? ErrorMessage { get; private set; }

        private TurnEvent(TurnEventKind kind) { Kind = kind; }

        public static TurnEvent AsrPartial(string text, float confidence)
            => new TurnEvent(TurnEventKind.AsrPartial) { Text = text, Confidence = confidence };

        public static TurnEvent AsrFinal(string text, float confidence, bool lowConfidence)
            => new TurnEvent(TurnEventKind.AsrFinal)
            {
                Text = text,
                Confidence = confidence,
                LowConfidence = lowConfidence,
            };

        public static TurnEvent LlmDelta(string text)
            => new TurnEvent(TurnEventKind.LlmDelta) { Text = text };

        public static TurnEvent Drive(DriveAction action)
            => new TurnEvent(TurnEventKind.DriveAction) { Action = action };

        public static TurnEvent Tts(byte[]? pcm16, int sampleRate, bool fromVoiceLib, string text)
            => new TurnEvent(TurnEventKind.TtsAudio)
            {
                Pcm16 = pcm16,
                SampleRate = sampleRate,
                FromVoiceLib = fromVoiceLib,
                Text = text,
            };

        public static TurnEvent StateChanged(StateDelta delta)
            => new TurnEvent(TurnEventKind.StateDelta) { State = delta };

        public static TurnEvent Error(string code, string message)
            => new TurnEvent(TurnEventKind.Error) { ErrorCode = code, ErrorMessage = message };
    }
}