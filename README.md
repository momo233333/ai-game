# 《副驾指令》游戏原型

本地 AI 语音交互驾驶模拟游戏。**主线为 V2.0 全 C# 架构**（见 [`IT方案1.md`](IT方案1.md)）：
Unity 负责渲染/物理/UI，AI 以**独立 .NET 8 伴生进程**运行（ASR / LLM / TTS 全本地推理），两者同语言、共享契约与纯逻辑类库。

> **开发进度 / 环境快照 / 已知问题 / 下一步** 见 [`开发进度.md`](开发进度.md) —— 每次开工先读它，完成阶段后更新它。

## 架构（V2.0）

```
Unity 主进程 (C#)  ──gRPC(localhost:50051)──►  AI 伴生进程 (C#, .NET 8)
渲染/物理/UI/麦克风                             VAD→ASR→Dialogue(LLM)→ActionParser→TTS
                                                  ▲ 共享 Copilot.Core（纯逻辑，netstandard2.1）
```

降级链（任一环节缺失都不影响启动）：

| 环节 | 首选 | 降级 | 当前状态 |
|------|------|------|--------|
| LLM | LLamaSharp + Vulkan（Qwen2.5-7B GGUF） | 规则引擎 | ✅ **llamasharp**（~100 tok/s） |
| ASR | sherpa-onnx 流式 paraformer | 不可用 | ✅ **sherpa-onnx**（int8/CPU，**partial 字幕**） |
| VAD | Silero（sherpa-onnx） | 能量阈值 | ✅ **silero**（7/7 样本恰好 1 次句尾） |
| TTS | 预生成语音库（~0ms） | sherpa-onnx VITS 实时合成 → 纯字幕 | **voicelib**（72 条真 wav，带防重复轮换）；VITS 未装 |
| 传输 | gRPC | — | ✅ **grpc**（localhost:50051） |

任一环节缺失都不影响启动：清空 `models/` 即退回 `规则引擎 + 语音库`，CLI 与全部测试照常可用。

## 目录

```
ai-game/
├── Copilot.sln                 # ★ V2.0 解决方案
├── src/
│   ├── Copilot.Contracts/      # proto → C# gRPC 契约（netstandard2.1）
│   ├── Copilot.Core/           # 纯逻辑：限幅/安全/信任情绪/语音库/编排（netstandard2.1，Unity 可引用）
│   └── Copilot.AIService/      # ★ AI 伴生进程（net8.0 控制台 + gRPC + CLI）
├── tests/
│   ├── Copilot.Tests/          # xUnit 107 项（含起真 gRPC 服务端的集成测试）
│   └── samples/                # 7 条指令语音（真 ASR 测试用）
├── models/                     # 模型权重（gitignore，用 tools/fetch_models.ps1 下）
├── data/sensitive_words.txt    # 共享敏感词表
├── voice-corpus/               # 台词语料 lines.csv + manifest.json + wavs/（72 条 24kHz）
├── tools/                      # fetch_models.ps1 / make_command_samples.ps1
│                               #   + command_samples.txt / voicelib_gen.py / voicelib_synth.py
├── ai-service/                 # ⚠️ 遗留：V1.0 Python 实现，不再维护
└── unity/                      # ⚠️ 遗留：V1.0 C# 骨架，待按 V2.0 重做
```

## 快速开始

> ⚠️ **必须用 `%LOCALAPPDATA%\dotnet\dotnet.exe`**：PATH 上的 `C:\Program Files\dotnet` 没有 SDK，也没有 ASP.NET Core 8 运行时。

```powershell
$env:PATH = "$env:LOCALAPPDATA\dotnet;$env:PATH"
cd e:\AI_Workplace\AI_Game\ai-game

# 0) 首次：下模型权重（约 4.6 GB，走 ModelScope 11–14 MB/s）+ 造指令语音样本
powershell -ExecutionPolicy Bypass -File tools\fetch_models.ps1
powershell -ExecutionPolicy Bypass -File tools\make_command_samples.ps1

# 1) 编译（0 警告 0 错误）+ 测试（107 项）
dotnet build Copilot.sln
dotnet test Copilot.sln

# 2) 看后端装配（有模型时应显示 llamasharp + sherpa-onnx + voicelib）
dotnet run --project src\Copilot.AIService -- --health

# 3) 单条指令端到端（真 LLM）
dotnet run --project src\Copilot.AIService -- --text "前面路口左转"
# [启动] 后端装配（含模型加载）2806 ms
#   [玩家] 前面路口左转
#   [动作] ↰左转 steering=-0.60 throttle=0.30 brake=0.00 hold=1200ms
# 妹妹> 左转？交给我，哥。
#   [TTS ] (语音库) 3.19s
#   [状态] 信任 60/100 (0) 情绪=CALM
#   [耗时] 动作 680 ms · 全回合 939 ms（不含模型加载）   ← 动作=说完→车动；冷启动；热会话约 0.23s
#   [LLM ] prefill 601ms · 生成 32 tok / 326ms = 98 tok/s · 合计 927ms

# 4) 交互式对话（/health /state /event <name> /quit）—— 一次加载多回合，第 2 回合起代表游戏内稳态
dotnet run --project src\Copilot.AIService

# 5) 语音交互（真 ASR）
dotnet run --project src\Copilot.AIService -- --wav tests\samples\left_turn.wav   # 音频文件 → 实时字幕 → 动作
dotnet run --project src\Copilot.AIService -- --mic --speak                       # 麦克风【按住空格说话】（松开即结束，默认模式）
dotnet run --project src\Copilot.AIService -- --mic --free --speak                # 麦克风【免提】（VAD 自动断句，直接说话停顿即结束）
dotnet run --project src\Copilot.AIService -- --vad tests\samples\left_turn.wav   # VAD 诊断（无麦克风也能验）
dotnet run --project src\Copilot.AIService -- --play "行，左转交给我"              # 仅验证音频输出

# 6) 起 gRPC 服务（供 Unity 接入）
dotnet run --project src\Copilot.AIService -- --server
```

> **没有模型也能跑**：`models/` 为空时 LLM 自动降级到规则引擎、ASR 不可用，`--text` / `--play` 与全部测试照常可用。

## 事件流（与 `copilot.proto` 对应）

```
文本/语音 ─→ asr_final ─→ drive_action(先执行) ─→ llm_delta(台词) ─→ tts_audio ─→ state_delta
```

- **指令先执行、台词后播放**：`DriveAction` 与文本流解耦，测试断言 `drive_action` 必须先于 `llm_delta`。
- **TTS 四段路由**：① 语音库精确命中（~0ms）→ ② 实时 VITS → ③ 语音库按意图兜底 → ④ 纯字幕；命中词条但 wav 缺失时**不谎报 voicelib**。
- **安全双层**：输入层命中敏感词直接出预置拒绝台词、不进 LLM；输出层再过一遍。
- **指令三重守卫**：白名单 → 场景规则（如高速不许停车）→ 数值限幅（转向 ±0.8）。

## gRPC 契约

| RPC | 用途 |
|-----|------|
| `VoiceTurn(stream AudioChunk) → stream TurnEvent` | 音频上行 → 事件流 |
| `TextTurn(TextRequest) → stream TurnEvent` | 文本回合（ASR 未就绪时的等价入口） |
| `SyncGameState(GameState) → Ack` | Unity 回推权威状态（信任/情绪/遥测） |
| `Ping(Empty) → Health` | 健康检查 + 各后端降级状态 |

改过 `src/Copilot.Contracts/proto/copilot.proto` 后重新生成代码：`dotnet build src\Copilot.Contracts\Copilot.Contracts.csproj`。

## 准备模型

一条命令搞定（`models/` 是 gitignore 的，脚本让它可复现）：

```powershell
powershell -ExecutionPolicy Bypass -File tools\fetch_models.ps1          # 全部（约 4.6 GB）
powershell -ExecutionPolicy Bypass -File tools\fetch_models.ps1 -Only llm # 只下 LLM
```

| 模块 | 落到 | 来源 / 大小 | 状态 |
|------|------|------------|------|
| LLM | `models/qwen2.5-7b-instruct-q4_k_m-0000{1,2}-of-00002.gguf` | ModelScope · Qwen2.5-7B-Instruct Q4_K_M · 3808+658 MB | ✅ 已验证（Vulkan，~100 tok/s） |
| ASR | `models/sherpa-onnx-streaming-paraformer-bilingual-zh-en/` | ModelScope 镜像 · int8 · encoder 158 + decoder 68 + tokens MB | ✅ 已验证 |
| VAD | `models/silero_vad.onnx` | GitHub releases · 0.6 MB | ✅ 已验证（7/7 样本恰好 1 次句尾） |
| TTS | `models/vits-zh/` | 可选（实时合成）；语音库已可用 | 未下载 |

> **下载源实测**：ModelScope 11–14 MB/s ✅ 首选；GitHub Releases 仅 0.01 MB/s ❌；常见 GitHub 代理 0.09–0.15 MB/s ❌；hf-mirror / huggingface.co 不可达 ❌。ASR 因此改用 ModelScope 的 int8 镜像（226 MB）而非 GitHub 的 1 GB tarball。

装上后**无需改代码**，`backend: auto` 会自动升级。`models/` 为空时全链路退回规则引擎 + 语音库。

**指令语音样本**（测试真 ASR 用，与语音库的「妹妹台词」不同）：

```powershell
powershell -ExecutionPolicy Bypass -File tools\make_command_samples.ps1   # 读 tools\command_samples.txt → tests\samples\*.wav
```

## 配置要点（`src/Copilot.AIService/appsettings.json`）

- `llm.backend` / `asr.backend` / `tts.backend` / `vad.backend`：`auto | <具体后端> | rule/none/voicelib/energy`
- `tts.voicelibManifest`：语音库 manifest 位置（默认 `voice-corpus/manifest.json`）
- `tts.sampleRate`：下行 PCM 采样率（默认 24000，与 `voice-corpus/wavs/` 一致）
- `server.url`：gRPC 监听地址（默认 `http://127.0.0.1:50051`）

## 遗留代码

`ai-service/`（Python 全套实现，269 单测）与 `unity/`（V1.0 C# 骨架，JSON-TCP）**保留为参考，不再维护**。
V2.0 的 Unity 侧将重做为「引用 `Copilot.Core` + 接 gRPC」，见 `开发进度.md` §8 下一步。