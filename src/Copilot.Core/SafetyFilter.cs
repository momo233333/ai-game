using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Copilot.Core
{
    /// <summary>
    /// 敏感词双层过滤（IT方案1.md V2.0 §5.4）：
    /// 输入层命中则不进 LLM，直接出预置拒绝台词；输出层再过一遍，命中则替换为通用吐槽。
    /// 词库规模小（百级），线性扫描 + 归一化匹配已足够（远低于 1ms）。
    /// </summary>
    public sealed class SafetyFilter
    {
        private readonly List<string> _words = new List<string>();

        public int Count => _words.Count;

        public SafetyFilter(IEnumerable<string>? words)
        {
            if (words == null) return;
            foreach (var w in words)
            {
                var norm = Normalize(w);
                if (norm.Length > 0) _words.Add(norm);
            }
        }

        public static SafetyFilter Empty => new SafetyFilter(null);

        /// <summary>从 UTF-8 文本文件加载词库（每行一个词，# 开头为注释）。</summary>
        public static SafetyFilter FromFile(string path)
        {
            if (!File.Exists(path)) return Empty;
            var lines = File.ReadAllLines(path, Encoding.UTF8);
            var words = new List<string>();
            foreach (var line in lines)
            {
                var t = line.Trim();
                if (t.Length == 0 || t.StartsWith("#", StringComparison.Ordinal)) continue;
                words.Add(t);
            }
            return new SafetyFilter(words);
        }

        /// <summary>是否命中敏感词。</summary>
        public bool Contains(string? text)
        {
            if (string.IsNullOrEmpty(text) || _words.Count == 0) return false;
            var norm = Normalize(text);
            for (int i = 0; i < _words.Count; i++)
            {
                if (norm.IndexOf(_words[i], StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        /// <summary>命中则返回 fallback，否则原样返回。</summary>
        public string Sanitize(string? text, string fallback)
            => Contains(text) ? fallback : (text ?? string.Empty);

        /// <summary>归一化：去空白与常见标点、统一小写，抵御“傻 逼/傻-逼”这类绕过。</summary>
        internal static string Normalize(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (var ch in text)
            {
                if (char.IsWhiteSpace(ch)) continue;
                if (IsNoise(ch)) continue;
                sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }

        private static bool IsNoise(char ch)
        {
            switch (ch)
            {
                case '、': case '，': case '。': case '！': case '？':
                case '；': case '：': case '“': case '”': case '‘':
                case '’': case '（': case '）': case '·': case '~':
                case '-': case '_': case '.': case ',': case '!':
                case '?': case ';': case ':': case '"': case '\'':
                    return true;
                default:
                    return false;
            }
        }
    }
}