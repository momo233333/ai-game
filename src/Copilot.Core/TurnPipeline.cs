using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Copilot.Core.Dialogue;
using Copilot.Core.Llm;
using Copilot.Core.Tts;

namespace Copilot.Core
{
    /// <summary>
    /// 单回合编排（IT方案1.md V2.0 §4.2 事件顺序）：
    /// asr_final → drive_action（先执行）→ llm_delta（台词）→ tts_audio → state_delta。
    ///
    /// **流式**：LLM 后端若实现 <see cref="IStreamingLlmBackend"/>，action 字段一闭合就立刻
    /// 下发 drive_action，不必等整句台词生成完 —— 这正是「指令先执行、台词后播放」的原意。
    /// 全链路纯逻辑，无 IO/无 Unity 依赖，可直接单测。
    /// </summary>
    public sealed class TurnPipeline
    {
        private const string RejectLine = "啧，别说这个，好好开车。";

        private readonly ILlmBackend _llm;
        private readonly TtsRouter _tts;
        private readonly SafetyFilter _safety;
        private readonly TrustState _trust;
        private readonly DialogueMemory _memory;

        public TurnPipeline(ILlmBackend llm, TtsRouter tts, SafetyFilter safety,
            TrustState? trust = null, DialogueMemory? memory = null)
        {
            _llm = llm ?? throw new ArgumentNullException(nameof(llm));
            _tts = tts ?? throw new ArgumentNullException(nameof(tts));
            _safety = safety ?? SafetyFilter.Empty;
            _trust = trust ?? new TrustState();
            _memory = memory ?? new DialogueMemory();
        }

        public string LlmBackendName => _llm.Name;
        public string TtsBackendName => _tts.Name;
        public TrustState Trust => _trust;
        public DialogueMemory Memory => _memory;

        /// <summary>把流式结果收集成列表（方便「一次性拿结果」的调用方与测试）。</summary>
        public async Task<IReadOnlyList<TurnEvent>> RunTextTurnAsync(
            string text, GameStateSnapshot state, CancellationToken ct = default)
        {
            var events = new List<TurnEvent>();
            await foreach (var e in RunTextTurnStreamAsync(text, state, ct).ConfigureAwait(false))
            {
                events.Add(e);
            }
            return events;
        }

        /// <summary>流式单回合：事件按产生顺序立即产出，gRPC / CLI 可直接转发。</summary>
        public async IAsyncEnumerable<TurnEvent> RunTextTurnStreamAsync(
            string text, GameStateSnapshot state,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            state ??= new GameStateSnapshot();

            // 0) 文本回合的“ASR 结果”就是输入本身
            yield return TurnEvent.AsrFinal(text ?? string.Empty, 1.0f, false);

            // 1) 输入层安全过滤：命中不进 LLM
            if (_safety.Contains(text))
            {
                var rejectAction = new DriveAction { Intent = Intents.Ignore, Confidence = 1f, HoldMs = 500 };
                yield return TurnEvent.Drive(rejectAction);
                yield return TurnEvent.LlmDelta(RejectLine);
                yield return BuildTtsEvent(null, RejectLine, rejectAction.Intent, state.Emotion);
                yield return ApplyState(0f, "玩家说了敏感内容");
                _memory.AddTurn(text ?? string.Empty, RejectLine);
                yield break;
            }

            var request = new LlmRequest
            {
                PlayerText = text ?? string.Empty,
                Prompt = PromptBuilder.BuildFull(state, _memory, text),
                // 两段式：静态前缀走 KV 复用，增量每回合变
                SystemPrompt = PromptBuilder.StaticSystem,
                UserText = PromptBuilder.BuildTurnInput(state, _memory, text),
                State = state,
            };

            // 2) 生成（有流式后端就边生成边下发；否则等整段结果）
            LlmOutput? output = null;
            GuardResult? guard = null;
            bool actionEmitted = false;
            Exception? failure = null;

            if (_llm is IStreamingLlmBackend streaming)
            {
                var enumerator = streaming.GenerateStreamAsync(request, ct).GetAsyncEnumerator(ct);
                try
                {
                    while (true)
                    {
                        LlmStreamChunk chunk;
                        try
                        {
                            if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) break;
                            chunk = enumerator.Current;
                        }
                        catch (Exception ex)
                        {
                            // try 块内不能 yield，这里只记录，下面统一产出错误事件
                            failure = ex;
                            break;
                        }

                        // ★ action 一闭合就立刻守卫 + 下发，此时 reply 还在生成
                        if (!actionEmitted && chunk.ActionReady is { } ready)
                        {
                            // 提前下发前就要做危险误判矫正（此刻还没有 trust_delta）
                            if (IsMisjudgedIgnore(ready.Intent, request.PlayerText))
                            {
                                ready.Intent = Intents.QuestionBack;
                                ready.Confidence = 0.4f;
                            }

                            guard = ActionLimits.Guard(ready, state);
                            actionEmitted = true;
                            yield return TurnEvent.Drive(guard.Action);
                        }

                        if (chunk.Final is { } final) output = final;
                    }
                }
                finally
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
            }
            else
            {
                try
                {
                    output = await _llm.GenerateAsync(request, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            }

            if (failure != null)
            {
                yield return TurnEvent.Error("llm_failed", failure.Message);
                yield break;
            }

            if (output == null)
            {
                yield return TurnEvent.Error("llm_failed", "LLM 未返回结果");
                yield break;
            }

            output = LlmOutputSanitizer.Sanitize(output, _tts.Library);

            // 危险判定归代码（见 IsMisjudgedIgnore）：非危险输入被判 IGNORE 时降级为反问，且不扣信任
            if (IsMisjudgedIgnore(output.Action.Intent, request.PlayerText))
            {
                output.Action.Intent = Intents.QuestionBack;
                output.Action.Confidence = 0.4f;
            }
            if (output.TrustDelta < 0f && !Intents.IsDangerousCommand(request.PlayerText))
            {
                output.TrustDelta = 0f;
            }

            // 3) 指令守卫（流式路径已在 action 闭合时做过；这里兜底非流式后端）
            guard ??= ActionLimits.Guard(output.Action, state);
            if (!actionEmitted)
            {
                yield return TurnEvent.Drive(guard.Action);
            }

            // 4) 台词（被拒绝时用强制话术）+ 输出层安全过滤
            var reply = guard.ForcedReply ?? output.Reply ?? string.Empty;
            reply = _safety.Sanitize(reply, RejectLine);

            // 5) TTS 双路由。先算结果：真实模型偶尔会返回空台词，
            //    此时用语音库按意图兜底的文案同时补上字幕与语音，避免「妹妹不吭声」。
            var ttsResult = _tts.Route(output.TtsHint, reply, guard.Action.Intent, state.Emotion);
            if (string.IsNullOrWhiteSpace(reply) && !string.IsNullOrWhiteSpace(ttsResult.Text))
            {
                reply = ttsResult.Text;
            }
            else if (ttsResult.FromVoiceLib && !string.Equals(reply, ttsResult.Text, StringComparison.Ordinal))
            {
                // 实时合成本不可用 → 音频只能来自「按意图兜底」的语音库台词。
                // 此时必须让**字幕与听到的一致**，否则玩家看到的和听到的是两句不同的话
                // （实测就是这样：「左转？交给我，小意思。」配的音频却是「行，左转交给我」）。
                // 装上实时 TTS 后 FromVoiceLib=false，LLM 原文会被保留。
                reply = ttsResult.Text;
            }

            yield return TurnEvent.LlmDelta(reply);
            yield return TurnEvent.Tts(ttsResult.Pcm16, ttsResult.SampleRate, ttsResult.FromVoiceLib, ttsResult.Text);

            // 6) 信任/情绪变化
            float delta = output.TrustDelta;
            if (guard.Intercepted) delta = Math.Min(delta, -2f);
            yield return ApplyState(delta, $"玩家：{text}");

            _memory.AddTurn(text ?? string.Empty, reply);
        }

        /// <summary>
        /// 危险判定归代码，不归 LLM：实测 7B 会把「今天天气不错」「谢了」「帮我买个菜」
        /// 都判成 IGNORE 并每次扣 5 点信任，十几轮下来信任直接崩。危险指令有特征词（<see cref="Intents.IsDangerousCommand"/>），
        /// 其余 IGNORE 一律视为误判 → 降级为「反问」。
        /// </summary>
        private static bool IsMisjudgedIgnore(string? intent, string? playerText)
            => string.Equals(intent, Intents.Ignore, StringComparison.Ordinal)
               && !Intents.IsDangerousCommand(playerText);

        /// <summary>应用 Unity 侧本地事件（撞车/闯红灯…），返回 state_delta 事件。</summary>
        public TurnEvent ApplyLocalEvent(string eventName)
        {
            float delta = _trust.ApplyLocalEvent(eventName);
            _memory.NoteTrustDelta(delta, $"本地事件 {eventName}");
            return TurnEvent.StateChanged(new StateDelta
            {
                Trust = _trust.Trust,
                Emotion = _trust.Emotion,
                TrustDelta = delta,
            });
        }

        private TurnEvent BuildTtsEvent(string? hint, string reply, string intent, Emotion emotion)
        {
            var result = _tts.Route(hint, reply, intent, emotion);
            return TurnEvent.Tts(result.Pcm16, result.SampleRate, result.FromVoiceLib, result.Text);
        }

        private TurnEvent ApplyState(float delta, string note)
        {
            float applied = _trust.Apply(delta);
            _memory.NoteTrustDelta(applied, note);
            return TurnEvent.StateChanged(new StateDelta
            {
                Trust = _trust.Trust,
                Emotion = _trust.Emotion,
                TrustDelta = applied,
            });
        }
    }
}