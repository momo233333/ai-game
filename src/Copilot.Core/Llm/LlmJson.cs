using System;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Copilot.Core.Llm
{
    /// <summary>
    /// LLM 输出的容错解析（沿用 V1.0 实战踩坑，IT方案1.md V2.0 §5.2）：
    /// ① 模型爱在 JSON 后补话/再给一个对象 → 只取首个平衡对象；
    /// ② 模型把台词库 URI 写成无引号裸值 → 自动补引号；
    /// ③ 彻底解析失败 → 正则兜底抽字段，绝不抛异常打断回合。
    /// </summary>
    public static class LlmJson
    {
        private static readonly Regex BareValue = new Regex(
            @":\s*(?!true|false|null)([A-Za-z][A-Za-z0-9_]*://[A-Za-z0-9_\-\./]+|[A-Za-z][A-Za-z0-9_\-\./]+)\s*(?=[,}])",
            RegexOptions.Compiled);

        private static readonly Regex FieldRegex = new Regex(
            @"""(?<k>[a-z_]+)""\s*:\s*(?<v>""[^""]*""|[^,}\s]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static LlmOutput Parse(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return Fallback();

            var obj = ExtractFirstObject(raw!);
            if (obj != null)
            {
                if (TryFromJson(obj, out var parsed)) return parsed;

                var quoted = QuoteBareValues(obj);
                if (quoted != obj && TryFromJson(quoted, out var parsed2)) return parsed2;
            }

            return FromLoose(raw!);
        }

        /// <summary>
        /// 从**尚未生成完**的文本里，尽力提取已经闭合的 action 对象。
        /// 用于流式下发：只要 action 那一段的右花括号出现，就能立刻把驾驶指令发出去，
        /// 不必等 reply 生成完（方案 §4.2「指令先执行、台词后播放」）。
        /// </summary>
        public static bool TryExtractAction(string? partial, out DriveAction action)
        {
            action = new DriveAction();
            if (string.IsNullOrEmpty(partial)) return false;

            int key = partial!.IndexOf("\"action\"", StringComparison.Ordinal);
            if (key < 0) return false;

            int start = partial.IndexOf('{', key);
            if (start < 0) return false;

            int end = FindMatchingBrace(partial, start);
            if (end < 0) return false; // 还没闭合

            return TryParseAction(partial.Substring(start, end - start + 1), out action);
        }

        private static bool TryParseAction(string jsonObject, out DriveAction action)
        {
            action = new DriveAction();
            try
            {
                using (var doc = JsonDocument.Parse(jsonObject))
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return false;

                    action = new DriveAction
                    {
                        Intent = GetStr(root, "intent", Intents.Ignore),
                        Steering = GetFloat(root, "steering"),
                        Throttle = GetFloat(root, "throttle"),
                        Brake = GetFloat(root, "brake"),
                        HoldMs = (int)GetFloat(root, "hold_ms"),
                        Confidence = GetFloat(root, "confidence"),
                    };
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>返回与 <paramref name="start"/> 处左花括号配对的右花括号下标；未闭合返回 -1。</summary>
        private static int FindMatchingBrace(string text, int start)
        {
            int depth = 0;
            bool inString = false, escaped = false;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (escaped) { escaped = false; continue; }
                    if (c == '\\') { escaped = true; continue; }
                    if (c == '"') inString = false;
                    continue;
                }

                if (c == '"') inString = true;
                else if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        /// <summary>扫描出首个括号平衡的 JSON 对象（正确处理字符串与转义）。</summary>
        public static string? ExtractFirstObject(string raw)
        {
            int start = raw.IndexOf('{');
            if (start < 0) return null;

            int depth = 0;
            bool inString = false, escaped = false;
            for (int i = start; i < raw.Length; i++)
            {
                char c = raw[i];
                if (inString)
                {
                    if (escaped) { escaped = false; continue; }
                    if (c == '\\') { escaped = true; continue; }
                    if (c == '"') inString = false;
                    continue;
                }

                if (c == '"') { inString = true; continue; }
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return raw.Substring(start, i - start + 1);
                }
            }
            return null;
        }

        /// <summary>给无引号的裸值补上引号（如 "reply":voicelib://L037）。</summary>
        public static string QuoteBareValues(string json)
            => BareValue.Replace(json, m => ":\"" + m.Groups[1].Value + "\"");

        public static bool TryFromJson(string json, out LlmOutput output)
        {
            output = Fallback();
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    var o = new LlmOutput();

                    if (root.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.Object)
                    {
                        o.Action.Intent = GetStr(action, "intent", Intents.Ignore);
                        o.Action.Steering = GetFloat(action, "steering");
                        o.Action.Throttle = GetFloat(action, "throttle");
                        o.Action.Brake = GetFloat(action, "brake");
                        o.Action.HoldMs = (int)GetFloat(action, "hold_ms");
                        o.Action.Confidence = GetFloat(action, "confidence");
                    }

                    o.Reply = GetStr(root, "reply", string.Empty);
                    o.TrustDelta = GetFloat(root, "trust_delta");

                    var hint = GetStr(root, "tts_hint", string.Empty);
                    o.TtsHint = string.IsNullOrWhiteSpace(hint) || hint == "null" ? null : hint;

                    output = o;
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static LlmOutput FromLoose(string raw)
        {
            var o = Fallback();
            foreach (Match m in FieldRegex.Matches(raw))
            {
                string k = m.Groups["k"].Value.ToLowerInvariant();
                string v = m.Groups["v"].Value.Trim().Trim('"');
                switch (k)
                {
                    case "intent": o.Action.Intent = v; break;
                    case "steering": o.Action.Steering = ParseFloat(v); break;
                    case "throttle": o.Action.Throttle = ParseFloat(v); break;
                    case "brake": o.Action.Brake = ParseFloat(v); break;
                    case "hold_ms": o.Action.HoldMs = (int)ParseFloat(v); break;
                    case "confidence": o.Action.Confidence = ParseFloat(v); break;
                    case "reply":
                        if (v.StartsWith("voicelib://", StringComparison.OrdinalIgnoreCase)) o.TtsHint = v;
                        else o.Reply = v;
                        break;
                    case "tts_hint": o.TtsHint = v == "null" ? null : v; break;
                    case "trust_delta": o.TrustDelta = ParseFloat(v); break;
                }
            }
            return o;
        }

        private static LlmOutput Fallback()
        {
            return new LlmOutput
            {
                Action = new DriveAction { Intent = Intents.QuestionBack, Confidence = 0.3f },
                Reply = "啊？你说啥？风太大没听清。",
                TrustDelta = 0f,
            };
        }

        private static string GetStr(JsonElement el, string name, string def)
            => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? def
                : def;

        private static float GetFloat(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var v)) return 0f;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetSingle(out float f)) return f;
            if (v.ValueKind == JsonValueKind.String && float.TryParse(v.GetString(),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out float s)) return s;
            return 0f;
        }

        private static float ParseFloat(string s)
            => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0f;
    }
}