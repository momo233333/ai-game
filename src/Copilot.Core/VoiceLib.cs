using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Copilot.Core
{
    /// <summary>语音库词条（manifest.json 一条）。</summary>
    public sealed class VoiceEntry
    {
        public string Id { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public string Emotion { get; set; } = "CALM";
        public IReadOnlyList<string> IntentTags { get; set; } = Array.Empty<string>();
        public int Priority { get; set; }
        public string WavPath { get; set; } = string.Empty;

        /// <summary>解析后的绝对路径（加载时填充）。</summary>
        public string? AbsoluteWavPath { get; set; }
    }

    /// <summary>
    /// TTS 路由 ①：预生成语音库检索（IT方案1.md V2.0 §5.5）。
    /// 高频固定台词直接命中 wav → ~0ms，无需实时合成。
    /// </summary>
    public sealed class VoiceLib
    {
        public int SampleRate { get; private set; } = 24000;
        public IReadOnlyList<VoiceEntry> Items => _items;
        public string? ManifestPath { get; private set; }

        private readonly List<VoiceEntry> _items = new List<VoiceEntry>();

        public static VoiceLib Empty => new VoiceLib();

        private VoiceLib() { }

        public static VoiceLib Load(string manifestPath)
        {
            var lib = new VoiceLib { ManifestPath = manifestPath };
            if (!File.Exists(manifestPath)) return lib;

            var dir = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? ".";
            using (var doc = JsonDocument.Parse(File.ReadAllText(manifestPath)))
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("sample_rate", out var sr) && sr.TryGetInt32(out int rate))
                {
                    lib.SampleRate = rate;
                }

                if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                {
                    return lib;
                }

                foreach (var el in items.EnumerateArray())
                {
                    var entry = new VoiceEntry
                    {
                        Id = GetString(el, "id"),
                        Text = GetString(el, "text"),
                        Emotion = GetString(el, "emotion"),
                        Priority = el.TryGetProperty("priority", out var p) && p.TryGetInt32(out int pr) ? pr : 0,
                        WavPath = GetString(el, "wav_path"),
                    };

                    var tags = new List<string>();
                    if (el.TryGetProperty("intent_tags", out var tagEl) && tagEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var t in tagEl.EnumerateArray())
                        {
                            var s = t.GetString();
                            if (!string.IsNullOrEmpty(s)) tags.Add(s!);
                        }
                    }
                    entry.IntentTags = tags;

                    if (!string.IsNullOrEmpty(entry.WavPath))
                    {
                        var abs = Path.GetFullPath(Path.Combine(dir, entry.WavPath.Replace('/', Path.DirectorySeparatorChar)));
                        entry.AbsoluteWavPath = File.Exists(abs) ? abs : null;
                    }

                    lib._items.Add(entry);
                }
            }

            return lib;
        }

        /// <summary>按词条 id 查找（用于解析 voicelib:// 提示）。</summary>
        public VoiceEntry? FindById(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            for (int i = 0; i < _items.Count; i++)
            {
                if (string.Equals(_items[i].Id, id!.Trim(), StringComparison.OrdinalIgnoreCase))
                    return _items[i];
            }
            return null;
        }

        /// <summary>精确文本匹配（归一化后）。</summary>
        public VoiceEntry? FindByText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var target = NormalizeText(text!);
            for (int i = 0; i < _items.Count; i++)
            {
                if (NormalizeText(_items[i].Text) == target) return _items[i];
            }
            return null;
        }

        /// <summary>按意图 + 情绪列出候选词条（情绪匹配优先、再按优先级降序）。用于防重复轮换。</summary>
        public IReadOnlyList<VoiceEntry> FindCandidates(string intent, Emotion emotion)
        {
            string emotionName = EmotionNames.ToWire(emotion);
            var matched = new List<VoiceEntry>();
            var anyEmotion = new List<VoiceEntry>();

            for (int i = 0; i < _items.Count; i++)
            {
                var e = _items[i];
                bool matchIntent = false;
                for (int j = 0; j < e.IntentTags.Count; j++)
                {
                    if (string.Equals(e.IntentTags[j], intent, StringComparison.Ordinal)) { matchIntent = true; break; }
                }
                if (!matchIntent) continue;

                if (string.Equals(e.Emotion, emotionName, StringComparison.OrdinalIgnoreCase)) matched.Add(e);
                else anyEmotion.Add(e);
            }

            matched.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            anyEmotion.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            matched.AddRange(anyEmotion);
            return matched;
        }

        /// <summary>按意图 + 情绪挑选最高优先级词条（情绪不匹配则退化为只看意图）。</summary>
        public VoiceEntry? FindByIntent(string intent, Emotion emotion)
        {
            var candidates = FindCandidates(intent, emotion);
            return candidates.Count > 0 ? candidates[0] : null;
        }

        /// <summary>取出裸 PCM16（去掉 RIFF 头）；wav 缺失或损坏返回 null。</summary>
        public byte[]? Pcm16(VoiceEntry entry)
        {
            if (entry?.AbsoluteWavPath == null) return null;
            try
            {
                return WavReader.ReadPcm16(entry.AbsoluteWavPath);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string GetString(JsonElement el, string name)
            => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? string.Empty
                : string.Empty;

        private static string NormalizeText(string text)
        {
            var sb = new System.Text.StringBuilder(text.Length);
            foreach (var ch in text)
            {
                if (char.IsWhiteSpace(ch)) continue;
                sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }
    }
}