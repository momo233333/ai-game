using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Copilot.Core
{
    /// <summary>
    /// 极简 JSON 解析/序列化（无第三方依赖，Unity 与 dotnet 通用）。
    /// 解析结果：Dictionary&lt;string, object&gt; / List&lt;object&gt; / string / double / bool / null。
    /// </summary>
    public static class SimpleJson
    {
        public static object Parse(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            int pos = 0;
            object value = ParseValue(json, ref pos);
            SkipWhitespace(json, ref pos);
            if (pos != json.Length) throw new FormatException("JSON trailing content at " + pos);
            return value;
        }

        public static bool TryParse(string json, out object value)
        {
            try
            {
                value = Parse(json);
                return true;
            }
            catch (Exception)
            {
                value = null;
                return false;
            }
        }

        public static string Write(object value)
        {
            var sb = new StringBuilder(64);
            WriteValue(sb, value);
            return sb.ToString();
        }

        // ---------- 读取辅助 ----------

        public static Dictionary<string, object> AsDict(object value)
        {
            return value as Dictionary<string, object>;
        }

        public static string GetString(Dictionary<string, object> dict, string key, string fallback = "")
        {
            object v;
            if (dict != null && dict.TryGetValue(key, out v) && v is string s) return s;
            return fallback;
        }

        public static double GetDouble(Dictionary<string, object> dict, string key, double fallback = 0.0)
        {
            object v;
            if (dict != null && dict.TryGetValue(key, out v))
            {
                if (v is double d) return d;
                if (v is int i) return i;
                if (v is long l) return l;
                if (v is float f) return f;
                if (v is string s)
                {
                    double parsed;
                    if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) return parsed;
                }
            }
            return fallback;
        }

        public static float GetFloat(Dictionary<string, object> dict, string key, float fallback = 0f)
        {
            return (float)GetDouble(dict, key, fallback);
        }

        public static int GetInt(Dictionary<string, object> dict, string key, int fallback = 0)
        {
            object v;
            if (dict != null && dict.TryGetValue(key, out v))
            {
                if (v is int i) return i;
                if (v is long l) return (int)l;
                if (v is double d) return (int)Math.Round(d);
                if (v is float f) return (int)Math.Round(f);
                if (v is string s)
                {
                    int parsed;
                    if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) return parsed;
                }
            }
            return fallback;
        }

        public static bool GetBool(Dictionary<string, object> dict, string key, bool fallback = false)
        {
            object v;
            if (dict != null && dict.TryGetValue(key, out v))
            {
                if (v is bool b) return b;
                if (v is double d) return Math.Abs(d) > double.Epsilon;
            }
            return fallback;
        }

        // ---------- 内部实现 ----------

        static void SkipWhitespace(string s, ref int pos)
        {
            while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t' || s[pos] == '\r' || s[pos] == '\n')) pos++;
        }

        static object ParseValue(string s, ref int pos)
        {
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length) throw new FormatException("JSON unexpected end of input");
            char c = s[pos];
            switch (c)
            {
                case '{': return ParseObject(s, ref pos);
                case '[': return ParseArray(s, ref pos);
                case '"': return ParseString(s, ref pos);
                case 't': Expect(s, ref pos, "true"); return true;
                case 'f': Expect(s, ref pos, "false"); return false;
                case 'n': Expect(s, ref pos, "null"); return null;
                default: return ParseNumber(s, ref pos);
            }
        }

        static void Expect(string s, ref int pos, string literal)
        {
            if (pos + literal.Length > s.Length || string.CompareOrdinal(s, pos, literal, 0, literal.Length) != 0)
                throw new FormatException("JSON invalid literal at " + pos);
            pos += literal.Length;
        }

        static Dictionary<string, object> ParseObject(string s, ref int pos)
        {
            var dict = new Dictionary<string, object>();
            pos++; // '{'
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == '}') { pos++; return dict; }
            while (true)
            {
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length || s[pos] != '"') throw new FormatException("JSON expected key at " + pos);
                string key = ParseString(s, ref pos);
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length || s[pos] != ':') throw new FormatException("JSON expected ':' at " + pos);
                pos++;
                dict[key] = ParseValue(s, ref pos);
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length) throw new FormatException("JSON unterminated object");
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == '}') { pos++; return dict; }
                throw new FormatException("JSON expected ',' or '}' at " + pos);
            }
        }

        static List<object> ParseArray(string s, ref int pos)
        {
            var list = new List<object>();
            pos++; // '['
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == ']') { pos++; return list; }
            while (true)
            {
                list.Add(ParseValue(s, ref pos));
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length) throw new FormatException("JSON unterminated array");
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == ']') { pos++; return list; }
                throw new FormatException("JSON expected ',' or ']' at " + pos);
            }
        }

        static string ParseString(string s, ref int pos)
        {
            var sb = new StringBuilder();
            pos++; // '"'
            while (true)
            {
                if (pos >= s.Length) throw new FormatException("JSON unterminated string");
                char c = s[pos++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (pos >= s.Length) throw new FormatException("JSON bad escape");
                char e = s[pos++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (pos + 4 > s.Length) throw new FormatException("JSON bad \\u escape");
                        sb.Append((char)ushort.Parse(s.Substring(pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        pos += 4;
                        break;
                    default: throw new FormatException("JSON unknown escape '\\" + e + "'");
                }
            }
        }

        static double ParseNumber(string s, ref int pos)
        {
            int start = pos;
            if (pos < s.Length && (s[pos] == '-' || s[pos] == '+')) pos++;
            while (pos < s.Length && (char.IsDigit(s[pos]) || s[pos] == '.' || s[pos] == 'e' || s[pos] == 'E' || s[pos] == '-' || s[pos] == '+')) pos++;
            string token = s.Substring(start, pos - start);
            double value;
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                throw new FormatException("JSON bad number '" + token + "' at " + start);
            return value;
        }

        static void WriteValue(StringBuilder sb, object value)
        {
            if (value == null) { sb.Append("null"); return; }
            if (value is string s) { WriteString(sb, s); return; }
            if (value is bool b) { sb.Append(b ? "true" : "false"); return; }
            if (value is double d) { WriteNumber(sb, d); return; }
            if (value is float f) { WriteNumber(sb, f); return; }
            if (value is int i) { sb.Append(i.ToString(CultureInfo.InvariantCulture)); return; }
            if (value is long l) { sb.Append(l.ToString(CultureInfo.InvariantCulture)); return; }
            if (value is IDictionary<string, object> dict)
            {
                sb.Append('{');
                bool first = true;
                foreach (var kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, kv.Key);
                    sb.Append(':');
                    WriteValue(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }
            if (value is System.Collections.IEnumerable seq)
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in seq)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteValue(sb, item);
                }
                sb.Append(']');
                return;
            }
            WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        static void WriteNumber(StringBuilder sb, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) { sb.Append("0"); return; }
            if (value == Math.Floor(value) && Math.Abs(value) < 1e15)
            {
                sb.Append(((long)value).ToString(CultureInfo.InvariantCulture));
                return;
            }
            sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }

        static void WriteString(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
