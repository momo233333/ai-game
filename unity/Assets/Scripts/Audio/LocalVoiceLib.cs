using System;
using System.IO;
using Copilot.Core;
using UnityEngine;

namespace Copilot.Audio
{
    /// <summary>
    /// 本地语音库播放：把 `voice-corpus/wavs/` 里的**预生成台词**直接播出来，不经 AI 服务端。
    ///
    /// 为什么需要它：`TtsRouter` 只在服务端回合内跑，所以玩法事件（她偏出车道了、救回来了）
    /// 过去只能出字幕 —— 她慌起来你听不见。
    ///
    /// 复用 Core 的 <see cref="VoiceLib"/>（与命令行端同一个实现），Unity 侧不另写一套检索。
    /// 拿不到 manifest 就**降级为纯字幕**：只打一条带"试过哪些路径"的 warning，不抛。
    ///
    /// ⚠️ **已知限制**：靠向上找仓库根（`Application.dataPath` → … → 含 `voice-corpus` 的目录）定位语音库，
    /// 这只在编辑器/开发环境成立；打包成 player 后需要把 `wavs/` 复制进 `StreamingAssets`。
    /// </summary>
    public sealed class LocalVoiceLib : MonoBehaviour
    {
        [Tooltip("相对仓库根的资源路径")]
        public string manifestRelativePath = "voice-corpus/manifest.json";

        VoiceLib _lib;
        bool _attempted;

        /// <summary>语音库可用（载入成功且至少有一条 wav 就位）。</summary>
        public bool IsReady
        {
            get
            {
                EnsureLoaded();
                if (_lib == null) return false;
                for (int i = 0; i < _lib.Items.Count; i++)
                {
                    if (!string.IsNullOrEmpty(_lib.Items[i].AbsoluteWavPath)) return true;
                }
                return false;
            }
        }

        public VoiceLib Library
        {
            get
            {
                EnsureLoaded();
                return _lib;
            }
        }

        void EnsureLoaded()
        {
            if (_attempted) return;
            _attempted = true;

            try
            {
                var root = FindRepoRoot(Application.dataPath);
                if (string.IsNullOrEmpty(root))
                {
                    Debug.LogWarning("[LocalVoiceLib] 找不到仓库根（从 " + Application.dataPath
                        + " 向上找过），本地语音降级为纯字幕");
                    return;
                }

                var path = Path.Combine(root, manifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path))
                {
                    Debug.LogWarning("[LocalVoiceLib] 语音库 manifest 不存在：" + path + "，本地语音降级为纯字幕");
                    return;
                }

                _lib = VoiceLib.Load(path);

                int ready = 0;
                for (int i = 0; i < _lib.Items.Count; i++)
                {
                    if (!string.IsNullOrEmpty(_lib.Items[i].AbsoluteWavPath)) ready++;
                }
                Debug.Log("[LocalVoiceLib] 本地语音库就绪：" + ready + "/" + _lib.Items.Count + " 条（" + path + "）");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[LocalVoiceLib] 载入失败，本地语音降级为纯字幕：" + ex.Message);
                _lib = null;
            }
        }

        /// <summary>按 id 播一条，返回**实际播出的文本**（没播到返回 null）。</summary>
        public string PlayById(string id, TtsPlayer player)
        {
            var lib = Library;
            if (lib == null || string.IsNullOrEmpty(id)) return null;
            return Play(lib.FindById(id), lib, player);
        }

        /// <summary>按「意图 + 情绪」挑一条播（同 <c>TtsRouter</c> 的候选规则）。</summary>
        public string PlayByIntent(string intent, Emotion emotion, TtsPlayer player)
        {
            var lib = Library;
            if (lib == null || string.IsNullOrEmpty(intent)) return null;

            var candidates = lib.FindCandidates(intent, emotion);
            for (int i = 0; i < candidates.Count; i++)
            {
                string played = Play(candidates[i], lib, player);
                if (played != null) return played;
            }
            return null;
        }

        static string Play(VoiceEntry entry, VoiceLib lib, TtsPlayer player)
        {
            if (entry == null || player == null) return null;

            var pcm = lib.Pcm16(entry);
            if (pcm == null || pcm.Length == 0) return null;

            return player.PlayLocal(pcm, lib.SampleRate, entry.Text) ? entry.Text : null;
        }

        /// <summary>
        /// 从起始目录向上找仓库根（含 `IT方案1.md` 或 `voice-corpus`）。
        ///
        /// ⚠️ 为什么这里自己写一份而没有复用：<c>Config.FindRepoRoot</c> 在
        /// **`Copilot.AIService`** 里，而 Unity 只引 Core/Contracts/Client 三个 DLL —— AIService 不在其中，
        /// Unity 里根本看不到它（这正是 Unity 编译报 `CS0103: Config 不存在` 的原因）。
        /// </summary>
        static string FindRepoRoot(string startDir)
        {
            var dir = new DirectoryInfo(startDir);
            for (int i = 0; i < 10 && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir.FullName, "IT方案1.md")) ||
                    Directory.Exists(Path.Combine(dir.FullName, "voice-corpus")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            return null;
        }
    }
}
