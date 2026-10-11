using System;
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
    /// `Copilot.Client` 走托管 `Grpc.Net.Client`，需要 HTTP/2；Unity 的 Mono BCL 是否支持 h2c
    /// 只有真跑一次才知道（见 开发进度.md 债 #13）。
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
                if (!task.Wait(TimeSpan.FromSeconds(120)))
                {
                    Debug.LogError("[SmokeTest] 超时 120s 未返回（传输层可能整个卡住）");
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

                int count = 0;
                int driveIndex = -1;
                int replyIndex = -1;
                int ttsIndex = -1;
                int audioBytes = 0;

                await foreach (var e in client.TextTurnAsync("前面路口左转"))
                {
                    switch (e.Kind)
                    {
                        case Core.TurnEventKind.DriveAction:
                            driveIndex = count;
                            Debug.Log($"[SmokeTest] drive_action intent={e.Action.Intent} "
                                      + $"steering={e.Action.Steering} hold={e.Action.HoldMs}");
                            break;
                        case Core.TurnEventKind.LlmDelta:
                            replyIndex = count;
                            Debug.Log($"[SmokeTest] llm_delta {e.Text}");
                            break;
                        case Core.TurnEventKind.TtsAudio:
                            ttsIndex = count;
                            audioBytes = e.Pcm16 != null ? e.Pcm16.Length : 0;
                            Debug.Log($"[SmokeTest] tts_audio {e.SampleRate}Hz voicelib={e.FromVoiceLib} bytes={audioBytes}");
                            break;
                        case Core.TurnEventKind.AsrFinal:
                            Debug.Log($"[SmokeTest] asr_final {e.Text}");
                            break;
                        case Core.TurnEventKind.Error:
                            Debug.LogError($"[SmokeTest] error {e.ErrorCode}: {e.ErrorMessage}");
                            break;
                    }
                    count++;
                }

                Debug.Log($"[SmokeTest] 共 {count} 个事件");

                bool ok = count > 0 && driveIndex >= 0 && driveIndex < replyIndex && ttsIndex > replyIndex && audioBytes > 0;
                Debug.Log(ok
                    ? "[SmokeTest] 通过：传输 / 事件映射 / 音频回传全部正常"
                    : "[SmokeTest] 不通过：事件顺序或音频缺失");

                return ok ? 0 : 1;
            }
        }
    }
}