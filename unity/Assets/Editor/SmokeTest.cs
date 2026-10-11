using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Copilot.Client;
using Core = Copilot.Core;
using UnityEditor;
using UnityEngine;

namespace Copilot.EditorTools
{
    /// <summary>
    /// 连通性自检：在 **Unity 的运行时**里真连一次 AI 伴生进程。
    ///
    /// 为什么必须有这个：脚本能在编辑器里编译通过，**不等于**运行时可用 ——
    /// `Copilot.Client` 走托管实现，而 Unity 的 Mono BCL 只支持 HTTP/1.1，
    /// 传输必须走 gRPC-Web 并把 HTTP 版本压到 1.1（见 开发进度.md 债 #13），
    /// 这些只有真跑一次才知道。
    ///
    /// 两条腿都要过：
    /// ① **文本回合**（TextTurn）—— 验传输 / 事件映射 / 音频回传；
    /// ② **语音回合**（VoiceTurn，喂真 wav）—— 验麦克风那条路径（真 ASR）。
    ///    这条特别重要：它才是游戏里实际走的路，gRPC-Web 化之后必须重验。
    ///
    /// 用法：
    /// ① 编辑器菜单：`副驾指令 / 连通性自检`（先起服务端 `--server`）
    /// ② 批处理：
    ///    Unity.exe -batchmode -projectPath unity \
    ///      -executeMethod Copilot.EditorTools.SmokeTest.Run -logFile logs\unity_smoke.log
    /// </summary>
    public static class SmokeTest
    {
        const string Url = "http://127.0.0.1:50051";

        [MenuItem("副驾指令/连通性自检")]
        public static void RunFromMenu()
        {
            Run();
        }

        /// <summary>批处理入口（也供菜单调用）。</summary>
        public static void Run()
        {
            Debug.Log("[SmokeTest] 开始连通性自检 " + Url);

            int code;
            try
            {
                // 必须放到线程池上跑：若在本线程 .GetResult()，主线程被阻塞，
                // 而 await 的续体默认要回 Unity 主线程 → 死锁（第一次就是这么卡住的）。
                var task = Task.Run(RunAsync);
                if (!task.Wait(TimeSpan.FromSeconds(180)))
                {
                    Debug.LogError("[SmokeTest] 超时 180s 未返回（传输层可能整个卡住）");
                    code = 1;
                }
                else
                {
                    code = task.Result;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[SmokeTest] 失败：" + ex);
                code = 1;
            }

            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        static async Task<int> RunAsync()
        {
            using (var client = new CopilotClient(Url))
            {
                var health = await client.HealthAsync();
                Debug.Log($"[SmokeTest] Ping 成功：llm={health.LlmBackend} asr={health.AsrBackend} "
                          + $"tts={health.TtsBackend} version={health.Version}");

                bool textOk = await TextTurnLeg(client);
                bool voiceOk = await VoiceTurnLeg(client);

                Debug.Log(textOk && voiceOk
                    ? "[SmokeTest] 通过：文本回合 + 语音回合（真 ASR）全部正常"
                    : $"[SmokeTest] 不通过：文本回合={(textOk ? "ok" : "fail")} 语音回合={(voiceOk ? "ok" : "fail")}");

                return textOk && voiceOk ? 0 : 1;
            }
        }

        /// <summary>腿 ①：文本回合。</summary>
        static async Task<bool> TextTurnLeg(CopilotClient client)
        {
            Debug.Log("[SmokeTest] ── 腿①：文本回合 ──");
            var summary = new TurnSummary();
            await foreach (var e in client.TextTurnAsync("前面路口左转"))
            {
                summary.Observe(e);
            }
            return summary.Report("文本", Core.Intents.TurnLeft);
        }

        /// <summary>腿 ②：语音回合（喂仓库里的真 wav，走真 ASR）。</summary>
        static async Task<bool> VoiceTurnLeg(CopilotClient client)
        {
            var wav = FindSampleWav("left_turn.wav");
            if (wav == null)
            {
                Debug.LogWarning("[SmokeTest] 找不到 tests/samples/left_turn.wav，跳过语音回合");
                return false;
            }

            // 样本就是 16kHz 单声道 PCM16 —— 正好是 AudioTurnRequest 要的格式
            var pcm = Core.WavReader.ReadPcm16(wav);
            Debug.Log($"[SmokeTest] ── 腿②：语音回合（{Path.GetFileName(wav)}，{pcm.Length / 2} 帧 @16kHz）──");

            var summary = new TurnSummary();
            await foreach (var e in client.VoiceTurnAsync(pcm))
            {
                summary.Observe(e);
            }
            return summary.Report("语音", Core.Intents.TurnLeft);
        }

        /// <summary>&lt;repo&gt;/tests/samples/&lt;name&gt;：从 Assets 往上两级就是仓库根。</summary>
        static string FindSampleWav(string name)
        {
            try
            {
                var unityProject = Directory.GetParent(Application.dataPath)?.FullName;
                var repo = unityProject == null ? null : Directory.GetParent(unityProject)?.FullName;
                if (repo == null) return null;

                var path = Path.Combine(repo, "tests", "samples", name);
                return File.Exists(path) ? path : null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SmokeTest] 解析样本路径失败：" + ex.Message);
                return null;
            }
        }

        /// <summary>把一轮事件收成可断言的摘要（事件顺序 + 关键字段）。</summary>
        sealed class TurnSummary
        {
            readonly List<string> _kinds = new List<string>();
            int _driveIndex = -1;
            int _replyIndex = -1;
            int _ttsIndex = -1;
            int _audioBytes;
            string _intent;
            string _asrText;
            string _error;

            public void Observe(Core.TurnEvent e)
            {
                _kinds.Add(e.Kind.ToString());

                switch (e.Kind)
                {
                    case Core.TurnEventKind.AsrFinal:
                        _asrText = e.Text;
                        break;
                    case Core.TurnEventKind.DriveAction:
                        _driveIndex = _kinds.Count - 1;
                        _intent = e.Action?.Intent;
                        break;
                    case Core.TurnEventKind.LlmDelta:
                        _replyIndex = _kinds.Count - 1;
                        break;
                    case Core.TurnEventKind.TtsAudio:
                        _ttsIndex = _kinds.Count - 1;
                        _audioBytes = e.Pcm16?.Length ?? 0;
                        break;
                    case Core.TurnEventKind.Error:
                        _error = e.ErrorCode + ":" + e.ErrorMessage;
                        break;
                }
            }

            public bool Report(string label, string expectedIntent)
            {
                Debug.Log($"[SmokeTest] {label}：识别「{_asrText}」意图={_intent} "
                          + $"音频={_audioBytes}B 事件流={string.Join("→", _kinds)}");

                if (_error != null) Debug.LogError($"[SmokeTest] {label} 收到错误事件 {_error}");

                bool ok = _error == null
                          && string.Equals(_intent, expectedIntent, StringComparison.Ordinal)
                          && _driveIndex >= 0 && _driveIndex < _replyIndex
                          && _ttsIndex > _replyIndex
                          && _audioBytes > 0;

                Debug.Log($"[SmokeTest] {label}：{(ok ? "ok（指令先于台词、带真音频）" : "FAIL")}");
                return ok;
            }
        }
    }
}