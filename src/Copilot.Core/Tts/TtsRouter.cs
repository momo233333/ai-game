using System;

namespace Copilot.Core.Tts
{
    /// <summary>实时合成引擎（伴生进程为 sherpa-onnx VITS；不可用时降级到语音库）。</summary>
    public interface IRealtimeTts
    {
        string Name { get; }
        bool IsAvailable { get; }
        int SampleRate { get; }
        byte[]? Synthesize(string text);
    }

    public sealed class TtsResult
    {
        public byte[]? Pcm16 { get; set; }
        public int SampleRate { get; set; } = 24000;
        public bool FromVoiceLib { get; set; }
        public string Text { get; set; } = string.Empty;
    }

    /// <summary>
    /// TTS 双路由（IT方案1.md V2.0 §5.5）：
    /// ① 预生成语音库（~0ms）→ ② 实时合成 → ③ 语音库按意图兜底 → ④ 仅字幕。
    /// 命中语音库但 wav 缺失时**不谎报 voicelib**，继续往下走（沿用 V1.0 的既有约定）。
    /// </summary>
    public sealed class TtsRouter
    {
        private readonly VoiceLib _lib;
        private readonly IRealtimeTts? _realtime;

        public TtsRouter(VoiceLib lib, IRealtimeTts? realtime = null)
        {
            _lib = lib ?? throw new ArgumentNullException(nameof(lib));
            _realtime = realtime;
        }

        /// <summary>语音库（供输出归一化等复用）。</summary>
        public VoiceLib Library => _lib;

        public string Name
        {
            get
            {
                if (_lib.Items.Count > 0 && _realtime != null && _realtime.IsAvailable)
                    return $"voicelib+{_realtime.Name}";
                if (_lib.Items.Count > 0) return "voicelib";
                return _realtime != null && _realtime.IsAvailable ? _realtime.Name : "none";
            }
        }

        public TtsResult Route(string? hint, string reply, string intent, Emotion emotion)
        {
            // ① hint 精确指向语音库词条
            var entry = ResolveHint(hint);
            var pcm = entry != null ? _lib.Pcm16(entry) : null;
            if (entry != null && pcm != null)
            {
                return new TtsResult
                {
                    Pcm16 = pcm,
                    SampleRate = _lib.SampleRate,
                    FromVoiceLib = true,
                    Text = entry.Text,
                };
            }

            // ①' reply 文本精确命中语音库
            var textEntry = _lib.FindByText(reply);
            var textPcm = textEntry != null ? _lib.Pcm16(textEntry) : null;
            if (textEntry != null && textPcm != null)
            {
                return new TtsResult
                {
                    Pcm16 = textPcm,
                    SampleRate = _lib.SampleRate,
                    FromVoiceLib = true,
                    Text = textEntry.Text,
                };
            }

            // ② 实时合成
            if (_realtime != null && _realtime.IsAvailable && !string.IsNullOrEmpty(reply))
            {
                var synth = _realtime.Synthesize(reply);
                if (synth != null && synth.Length > 0)
                {
                    return new TtsResult
                    {
                        Pcm16 = synth,
                        SampleRate = _realtime.SampleRate,
                        FromVoiceLib = false,
                        Text = reply,
                    };
                }
            }

            // ③ 语音库按意图兜底（保证玩家总能听到一句，符合“妹妹必有回应”）
            var byIntent = PickByIntent(intent, emotion);
            var intentPcm = byIntent != null ? _lib.Pcm16(byIntent) : null;
            if (byIntent != null && intentPcm != null)
            {
                return new TtsResult
                {
                    Pcm16 = intentPcm,
                    SampleRate = _lib.SampleRate,
                    FromVoiceLib = true,
                    Text = byIntent.Text,
                };
            }

            // ④ 仅字幕（缺资产/实时引擎不可用时）
            return new TtsResult { Pcm16 = null, SampleRate = _lib.SampleRate, FromVoiceLib = false, Text = reply };
        }

        /// <summary>本意图上次念过的词条 id（用于防重复：同一意图连续触发时换一句）。</summary>
        private string? _lastIntentEntryId;

        /// <summary>
        /// 按意图挑词条，并**优先避开上一句** —— 否则同一个意图（尤其反问）会连续回同一条台词，
        /// 听感上像「她只会说这一句」。只在候选里挑有 wav 的。
        /// </summary>
        private VoiceEntry? PickByIntent(string intent, Emotion emotion)
        {
            var candidates = _lib.FindCandidates(intent, emotion);
            if (candidates.Count == 0) return null;

            foreach (var c in candidates)
            {
                if (c.Id == _lastIntentEntryId) continue;
                if (_lib.Pcm16(c) == null) continue;
                _lastIntentEntryId = c.Id;
                return c;
            }

            // 全都播过或都没 wav → 退回第一条有 wav 的
            foreach (var c in candidates)
            {
                if (_lib.Pcm16(c) != null)
                {
                    _lastIntentEntryId = c.Id;
                    return c;
                }
            }
            return null;
        }

        private VoiceEntry? ResolveHint(string? hint)
        {
            if (string.IsNullOrWhiteSpace(hint)) return null;
            const string prefix = "voicelib://";
            if (!hint!.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            var id = hint.Substring(prefix.Length).Trim();
            return _lib.FindById(id);
        }
    }
}