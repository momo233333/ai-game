# 《副驾指令》IT 实施方案（技术开发版）

> **项目代号**：副驾指令（暂定）
> **文档版本**：V2.0（全 C# 重构版，替代 V1.0 Python 双进程方案）
> **更新日期**：2026-10-09
> **适用范围**：本地 AI 语音交互驾驶模拟游戏全技术栈
> **目标环境**：Windows 11 + AMD RX 9070 XT (16GB VRAM)
> **语言/运行时**：全链路 C# / .NET 8（AI 伴生进程）+ C# / Unity 2022.3（游戏本体）

---

## 变更说明（V1.0 → V2.0）

1. **语言统一为 C#**：V1.0 的 AI 服务用 Python 编写，与 Unity（C#）跨语言、跨运行时，契约要靠 protobuf 手工维护两套 DTO、模型推理依赖各自的环境，适配性差。V2.0 全链路改用 C#，AI 侧以 **独立 .NET 8 伴生进程** 运行，与 Unity **同语言、同生态、可共享类库与契约**。
2. **实施顺序重构**：由「先搭车辆物理、后接语音」改为「**先做语音交互 → 测试验收 → 再接入 3D 模型**」。语音交互核心在伴生进程内即可命令行自测，不依赖 Unity 工程与美术资源，可先跑通、先验收。
3. **推理栈 C# 化**：LLM 改用 LLamaSharp、ASR/VAD/TTS 改用 sherpa-onnx，全部有 .NET 绑定与 Vulkan/DirectML 后端，替代旧方案 llama-cpp-python / FunASR / GPT-SoVITS。

---

## 目录

1. [系统总体架构](#1-系统总体架构)
2. [环境与平台资源清单](#2-环境与平台资源清单)
3. [技术选型（C# 组件）](#3-技术选型c组件)
4. [接口协议设计](#4-接口协议设计核心契约)
5. [语音交互核心（AI 伴生进程）实现方案](#5-语音交互核心ai-伴生进程实现方案)
6. [Unity 侧实现方案（3D / 车辆 / 音频 / 状态）](#6-unity-侧实现方案)
7. [Prompt 工程规范](#7-prompt-工程规范)
8. [测试与质量保障](#8-测试与质量保障)
9. [关键技术风险与验证方案](#9-关键技术风险与验证方案)
10. [构建与发布技术方案](#10-构建与发布技术方案)
11. [实施顺序（语音交互优先）](#11-实施顺序语音交互优先)

---

## 1. 系统总体架构

### 1.1 架构原则

- **同语言双进程**：Unity（渲染/物理/UI）与 AI 伴生进程（ASR/VAD/LLM/TTS）**均用 C#/.NET**，通过本地 gRPC 通信，互不阻塞、可独立重启调试。
- **共享契约与类库**：事件 DTO、驾驶指令白名单、信任/情绪状态机、动作限幅等**纯逻辑抽为共享项目**，两进程引用同一份代码，杜绝“协议两套手工同步”的跨语言痛点。
- **全本地推理**：ASR / LLM / TTS 全部本地运行，零云端依赖，零数据外发。
- **流水线并行**：ASR→LLM→TTS 采用流式 pipeline，各环节重叠执行而非串行等待。
- **降级兜底**：每个 AI 环节都有失败降级路径（实时 TTS 失败→预生成语音库；LLM 输出非法→规则引擎兜底）。
- **语音先行**：伴生进程自包含麦克风采集与音频回放，可在**无 Unity、无 3D 资源**的情况下命令行跑通并自测，是 S 阶段的第一块里程碑。

### 1.2 架构图

```
┌─────────────────────────── Unity 主进程 (C#, Unity 2022.3) ────────────────────┐
│                                                                               │
│  InputLayer          GameCore（3D）             PresentationLayer              │
│  ┌─────────┐    ┌──────────────────┐    ┌────────────────────────┐            │
│  │麦克风采集 │    │ VehicleController │    │ UIManager(对话气泡/表情) │            │
│  │(Unity    │    │ (WheelCollider×4) │    │ AudioMixer(3总线)      │            │
│  │ Audio)   │    ├──────────────────┤    │ CameraDirector        │            │
│  └────┬────┘    │ GameStateManager  │    │ 3D 角色模型/动画       │            │
│       │         │ (信任/情绪/评分)   │    └────────────────────────┘            │
│       ↓         ├──────────────────┤                  ↑                      │
│  ┌─────────┐    │ ActionExecutor    │──────────────────┘                      │
│  │AudioBus │    │ (指令→物理层映射  │                                          │
│  │(PCM推流)│    │  +限幅+插值)     │                                          │
│  └────┬────┘    └────────▲─────────┘                                          │
│       │                  │                                                    │
│  ┌────▼──────────────────┴────┐                                               │
│  │      AIClient (gRPC .NET)   │  ← 含断线重连/超时/心跳                        │
│  └────────────┬───────────────┘                                               │
└───────────────┼───────────────────────────────────────────────────────────────┘
                │ localhost:50051 (gRPC/protobuf，含流式接口)
┌───────────────▼──────────────── AI 伴生进程 (C#, .NET 8) ──────────────────────┐
│                                                                               │
│  ┌──────────┐   ┌──────────┐   ┌───────────────┐   ┌──────────────┐          │
│  │ VAD      │──→│ ASR      │──→│ DialogueCore   │──→│ ActionParser │          │
│  │ Silero   │   │ paraformer│  │ (Prompt组装/    │   │ (JSON Schema │          │
│  │ (断句)    │   │ streaming│  │  记忆管理/     │   │  校验+白名单) │          │
│  └──────────┘   └──────────┘   │  LLamaSharp)  │   └──────┬───────┘          │
│                                └───────────────┘          ↓                  │
│                 ┌─────────────────────────────┐   ┌──────────────┐          │
│                 │ SafetyFilter (敏感词+规则)    │──→│ TTSRouter    │          │
│                 └─────────────────────────────┘   │ ①语音库检索   │          │
│                                                    │ ②sherpa-onnx │          │
│                                                    │   VITS实时合成│          │
│                                                    └──────────────┘          │
└───────────────────────────────────────────────────────────────────────────────┘
                GPU: Vulkan（LLM）/ DirectML（ASR/TTS）—— 16GB VRAM 预算见 §6.4

共享项目 Copilot.Contracts（两进程同引用）：
  proto/*.proto → 生成 C# gRPC 服务/客户端 + DTO
  ActionLimits（限幅/白名单） · StateMachine（信任/情绪） · PureLogic

```

---

## 2. 环境与平台资源清单

### 2.1 目标运行环境（玩家侧）

| 类别 | 要求 | 说明 |
|------|------|------|
| 操作系统 | Windows 11 23H2+ (x64) | 主目标平台；暂不做 Linux/macOS |
| GPU（推荐） | AMD RX 9070 XT 16GB 或同档 NVIDIA 16GB+ | Vulkan / DirectML 后端 |
| GPU（最低） | 8GB 显存中端卡 | 走 8GB 降级模式（§9） |
| 内存 | 16GB+ | LLM KV cache + Unity 场景 |
| 硬盘 | 预留 15GB+ | 模型 + 工程 + 语音库 |
| 音频 | 麦克风 + 扬声器/耳机 | 半双工对话，无特殊声卡要求 |

### 2.2 开发环境（必装）

| 工具 | 版本 | 用途 |
|------|------|------|
| Windows 11 | 23H2+ | 开发/目标一致 |
| GPU 驱动 | 最新 WHQL | AMD Adrenalin / NVIDIA Studio |
| **Unity** | **2022.3 LTS + URP 14** | 游戏工程（3D/车辆/UI/角色） |
| **.NET SDK** | **8.0 LTS** | AI 伴生进程 + 共享类库 + 无头测试 |
| C# IDE | Rider 2024+ / VS 2022 | 伴生进程与共享层开发 |
| Git | LFS 启用 | 模型权重/音频资产走 LFS |
| CI | GitHub Actions (windows runner) | 自动化测试（§8） |

### 2.3 运行时依赖（Native 库，随包分发）

| 依赖 | 来源 | 说明 |
|------|------|------|
| `llama.dll`（Vulkan build） | LLamaSharp 后端包 / llama.cpp 预编译 | LLM 推理，A 卡走 Vulkan |
| `onnxruntime.dll`（DirectML） | sherpa-onnx 依赖 | ASR/VAD/TTS，Windows 走 DirectML 加速 |
| `onnxruntime_providers_shared.dll` 等 | sherpa-onnx 依赖 | DirectML provider |

### 2.4 模型资源（本地推理，随包或首启下载）

| 模块 | 模型 | 大小（约） | 来源 |
|------|------|-----------|------|
| LLM | Qwen2.5-7B-Instruct GGUF **Q4_K_M** | ~4.4GB | ModelScope / HuggingFace（GGUF 分片） |
| ASR | sherpa-onnx 流式 paraformer 中文（含标点模型） | ~200MB | k2-fsa/sherpa-onnx 模型仓库 |
| VAD | silero-vad ONNX | ~2MB | k2-fsa/sherpa-onnx |
| TTS（实时） | VITS 中文 ONNX（备选 melo-tts） | 数十~百 MB | k2-fsa/sherpa-onnx |
| 语音库 | 预生成台词 wav（70+ 条，24kHz） | 数十 MB 内 | 本地脚本合成 |

### 2.5 NuGet / 插件清单

| 包 | 版本策略 | 用途 |
|------|---------|------|
| `LLamaSharp` + `LLamaSharp.Backend.Vulkan` | 锁定 aligned 版本 | 对话 LLM |
| `SherpaOnnx`（官方 C# 包） | 锁定 aligned 版本 | ASR / VAD / TTS |
| `NAudio` | 稳定版 | 伴生进程麦克风采集 / wav 读写（Unity 内用 `Microphone`） |
| `Google.Protobuf` + `Grpc.Tools` + `Grpc.Net.Client` / `Grpc.AspNetCore` | 锁定 aligned 版本 | 进程间通信 |
| Unity 侧 | 引用共享项目编译产出 dll + sherpa-onnx netstandard2.0 程序集 | 表现层 |

---

## 3. 技术选型（C# 组件）

| 模块 | V1.0（Python，弃用） | V2.0（C#） | 说明 |
|------|---------------------|-----------|------|
| LLM | llama-cpp-python / llama-server | **LLamaSharp**（llama.cpp 的 .NET 绑定） | 内嵌进程内存加载 GGUF，支持流式 token + GBNF grammar 约束，Vulkan/CUDA/CPU 后端 |
| 结构化输出 | 手写 GBNF + JSON schema | **LLamaSharp `SafeLLamaGrammarHandle`（GBNF）** | 从解码层强制合法 JSON，与旧方案同思路但同进程 |
| ASR | FunASR paraformer（Python/torch） | **sherpa-onnx 流式 paraformer** | 同一 paraformer 体系的 ONNX 版，原生 C# API，免 torch |
| VAD | Silero VAD（onnxruntime-py） | **sherpa-onnx 内置 Silero VAD** | 与 ASR 共用 onnxruntime，无需第二个运行时 |
| TTS | GPT-SoVITS（torch，难装） | **语音库 + sherpa-onnx VITS** | 高频台词走预生成 wav（~0ms），自由句实时合成 |
| 进程通信 | gRPC（Python grpcio） | **gRPC for .NET**（本地 localhost） | 同语言，proto 生成 C# 客户端/服务端，共享 DTO |
| 麦克风 | Unity `Microphone` | 伴生进程 `NAudio`（自测）/ Unity `Microphone`（游戏） | 语音先行阶段用 NAudio 自测，游戏阶段复用 Unity 采集 |

---

## 4. 接口协议设计（核心契约）

### 4.1 Protobuf 定义

```protobuf
syntax = "proto3";
package copilot;

service AIService {
  // 上行音频流 → 下行完整AI响应流（双向流式）
  rpc VoiceTurn(stream AudioChunk) returns (stream TurnEvent);
  // 文本回合（无 ASR 时的等价入口，便于自测/回归）
  rpc TextTurn(TextRequest) returns (stream TurnEvent);
  // 状态同步（Unity → AI，每次场景/数值变化时调用）
  rpc SyncGameState(GameState) returns (Ack);
  // 服务健康检查
  rpc Ping(Empty) returns (Health);
}

message AudioChunk {
  bytes pcm16 = 1;          // 16kHz mono PCM
  int64 seq = 2;
  bool end_of_stream = 3;   // 玩家主动结束或静音超时
}

message TextRequest { string text = 1; }

message GameState {
  float trust = 1;          // 0-100
  Emotion emotion = 2;
  SceneContext scene = 3;   // 场景ID/天气/时间段
  VehicleTelemetry telemetry = 4;
  string level_id = 5;
}

enum Emotion { CALM = 0; ANNOYED = 1; NERVOUS = 2; HAPPY = 3; }

message VehicleTelemetry {
  float speed_kmh = 1;
  int32 gear = 2;
  bool at_intersection = 3;
  string obstacle_summary = 4;   // 预置枚举拼接，如 "前方红灯;左侧有车"
}

// 下行事件流：按产生顺序推送，实现流式体验
message TurnEvent {
  oneof payload {
    AsrPartial asr_partial = 1;   // ASR中间结果（UI实时字幕）
    AsrFinal   asr_final = 2;     // 最终识别文本
    LlmDelta   llm_delta = 3;     // LLM流式文本片段（打字机效果）
    TtsAudio   tts_audio = 4;     // TTS音频分块（边合成边播）
    DriveAction drive_action = 5; // 结构化驾驶指令（文本未播完即可先执行）
    StateDelta state_delta = 6;   // 信任值/情绪变化
    ErrorEvent error = 7;
  }
}

message DriveAction {
  float steering = 1;   // [-1, 1]
  float throttle = 2;   // [0, 1]
  float brake = 3;      // [0, 1]
  int32 hold_ms = 4;    // 动作持续时间
  string intent = 5;    // TURN_LEFT / TURN_RIGHT / ACCELERATE / BRAKE /
                        // LANE_CHANGE / PARK / IGNORE / QUESTION_BACK ...
  float confidence = 6; // LLM自报置信度
}

message TtsAudio {
  bytes pcm16 = 1;          // 24kHz mono 裸 PCM16
  bool from_voicelib = 2;   // true=预生成库, false=实时合成
  int32 sample_rate = 4;    // 24kHz
}

```

### 4.2 关键设计说明

| 决策 | 理由 |
|------|------|
| **双向流式而非请求-响应** | ASR 中间结果实时上屏字幕；TTS 音频分块边合成边播，降低首音延迟 |
| **DriveAction 独立于文本流** | 驾驶指令在 LLM 生成前段 token 时即可提取，指令**先执行**、台词**后播放**，符合“妹妹边说边打方向”的真实感 |
| **obstacle_summary 枚举拼接而非自由文本** | 避免 Unity 侧序列化复杂场景；防止 LLM 幻觉编造不存在的路况 |
| **共享契約项目** | proto 由 `Grpc.Tools` 编译为 C#，两个进程引用同一 `Copilot.Contracts` 程序集，无跨语言 DTO 三套同步问题 |
| **StateDelta 由 AI 回推** | 信任值变化逻辑集中在 Prompt/规则层，Unity 只做表现 |

---

## 5. 语音交互核心（AI 伴生进程）实现方案

伴生进程为 .NET 8 控制台程序 `Copilot.AIService`，自包含：`NAudio` 麦克风采集 + gRPC 服务端 + LLamaSharp/sherpa-onnx 推理。**不依赖 Unity，可单独命令行自测**（语音交互阶段的第一里程碑）。

### 5.1 VAD + ASR 管线（sherpa-onnx）

```csharp
// 核心流程（伪代码）
var vad = new VoiceActivityDetector(vadConfig);      // sherpa-onnx Silero VAD
var asr = new OnlineRecognizer(asrConfig);            // sherpa-onnx 流式 paraformer

async IAsyncEnumerable<TurnEvent> VoiceTurn(IAsyncEnumerable<AudioChunk> chunks)
{
    OnlineStream stream = asr.CreateStream();
    var buffer = new RingBuffer(sampleRate: 16000);
    await foreach (var chunk in chunks)
    {
        buffer.Write(chunk.Pcm16);
        // VAD 判定起声与静音
        bool speech = vad.IsSpeech(chunk);
        // 流式喂 ASR，产生 partial 用于实时字幕
        stream.AcceptWaveform(sampleRate, samples);
        while (asr.IsReady(stream)) asr.Decode(stream);
        yield return AsrPartial(asr.GetResult(stream));
        if (speechStarted && vad.SilenceExceeded(buffer, 600))
            break;
    }
    stream.InputFinished();
    yield return AsrFinal(asr.GetResult(stream));
}
```

**要点**：

- 麦克风采集在伴生进程用 `NAudio.WaveInEvent`（语音交互自测），正式游戏采集移到 Unity `Microphone.Start`（480 帧环形缓冲），重采样 16kHz 推流。两个入口共用同一套 `AudioBus` 抽象。
- ASR 置信度 < 0.6 时 `asr_final` 附低置信标记，DialogueCore 触发“反问”分支（“啊？你说啥？风太大没听清”）——技术缺陷转化为玩法。
- VAD 参数需在实际噪音环境实测调优，预留配置热加载。

### 5.2 LLM 封装（LLamaSharp）

```csharp
var @params = new ModelParams("models/qwen2.5-7b-instruct-q4_k_m.gguf")
{
    ContextSize = 4096,
    GpuLayerCount = 99,          // 全量 offload 到 GPU（Vulkan）
    UseMemoryLock = true,
};
using var model = LLamaWeights.LoadFromFile(@params);
using var ctx = model.CreateContext(@params);
var executor = new InteractiveExecutor(ctx);

// GBNF 结构化输出：从解码层强制合法 JSON
var grammar = Grammar.Parse(ACTION_GBNF);
using var grammarHandle = ctx.CreateGrammarHandle(grammar);
```

```bash
# GBNF 语义等价（LLamaSharp 内以 Grammar.Parse 加载）
root ::= "{\"action\":" action ",\"tts_hint\":" hint ",\"reply\":\"" text "\",\"trust_delta\":" int "}"
action ::= "{\"intent\":\"" intent-enum "\",\"steering\":" num ",\"throttle\":" num ",\"brake\":" num ",\"hold_ms\":" int "}"
intent-enum ::= "TURN_LEFT" | "TURN_RIGHT" | "ACCELERATE" | "BRAKE" | "LANE_CHANGE" | "PARK" | "IGNORE" | "QUESTION_BACK"
hint ::= "null" | "\"voicelib://" path "\""
num ::= "-"? digit ("." digit digit)?
```

> 注意：grammar 中 **action 字段排在 reply 之前生成**——保证 Unity 最先拿到驾驶指令。

### 5.3 对话上下文管理（记忆系统）

```
上下文窗口预算 (n_ctx=4096)：
├── 系统Prompt(人格+规则)          ~800 tokens  [常驻]
├── 记忆区                         ~600 tokens  [滚动]
│   ├── 局内摘要(每5轮压缩一次)
│   └── 旧账队列(重要负面事件, FIFO, 最多5条)
├── 当前GameState注入              ~200 tokens  [每轮刷新]
├── 近期对话原文(最近8轮)           ~1600 tokens [滑动窗口]
└── 本轮ASR文本+输出格式提示        ~200 tokens
预留输出空间                        ~600 tokens
```

**实现要点**：

- 摘要压缩由同一 LLM 异步执行（玩家非说话间隙触发）。
- “旧账”写入条件：`trust_delta <= -5` 或玩家辱骂事件 → 存入 SQLite（`memory.db`，C# 用 `Microsoft.Data.Sqlite`，按 save_slot 分区），跨关卡持久化。
- 系统 Prompt 人格描述与 GameState 分离注入，GameState 模板化。

### 5.4 动作解析与安全层

```csharp
DriveAction ParseAndGuard(LlmOutput output, GameState state)
{
    var action = output.Action;
    // 1. Schema 已由 GBNF 约束保证，此处做值域校验
    action.Steering = Math.Clamp(action.Steering, -0.8f, 0.8f);
    action.Throttle = Math.Clamp(action.Throttle, 0f, 1f);
    // 2. 危险指令拦截：白名单外 intent → 强制 IGNORE + 反问话术
    if (!ActionLimits.AllowedIntents.Contains(action.Intent))
        return FallbackQuestionBack(state);
    // 3. 场景规则校验：高速行驶中 intent=PARK → 拒绝执行
    if (ViolatesSceneRules(action, state))
        return RefuseWithLine(action, state);
    return action;
}
```

**SafetyFilter 双层设计**：

- **输入层**：玩家 ASR 文本过敏感词库（AC 自动机，<1ms）→ 命中则不进入 LLM，直接返回预置拒绝台词。
- **输出层**：LLM reply 文本过同一词库 + 正则兜底 → 命中则替换为该情绪档通用吐槽台词（语音库必有）。

### 5.5 TTS 双路由策略

| 路由 | 适用场景 | 延迟 | 实现 |
|------|---------|------|------|
| **① 预生成语音库** | 高频固定台词：动作确认、吐槽（800-1200 条）、教学/拒绝话术 | **~0ms** | `manifest.json` 索引 `{text, wav_path, emotion, intent_tags[]}`；检索 = 精确匹配→关键词匹配（备选：语义向量 ONNX） |
| **② 实时合成** | 自由闲聊、含动态信息的台词（“现在都{speed}码了你让我{intent}？”） | 首块 300-500ms | sherpa-onnx VITS 流式分句合成：LLM 每生成完整一句即触发合成，与后续 token 生成并行 |

**关键机制**：LLM 输出含 `tts_hint` 字段（`voicelib://xxx` 或 null）——Prompt 引导 LLM 语义匹配语音库时直接引用库内台词（语音库文本清单注入系统 Prompt），匹配不上才自由发挥走实时 TTS。**目标：70% 以上回复命中语音库。**

---

## 6. Unity 侧实现方案

### 6.1 车辆控制层（3D）

- 基于 **WheelCollider** 标准方案（前轮 steerAngle、后轮 motorTorque、四轮 brakeTorque）。
- **AI 指令不直接写 Collider**，经 `ActionExecutor` 中间层：

```csharp
// 指令平滑化：避免LLM离散指令导致车辆抖动
public class ActionExecutor {
    DriveAction _current; float _elapsed;
    void FixedUpdate() {  // 50Hz
        _elapsed += Time.fixedDeltaTime * 1000;
        float t = Mathf.Clamp01(_elapsed / _current.HoldMs);
        float steer = Mathf.SmoothStep(0, _current.Steering * MAX_STEER, t);
        _vehicle.ApplyInput(steer, _current.Throttle, _current.Brake);
    }
    public void Execute(DriveAction a) { _current = ClampAction(a); _elapsed = 0; }
}
```

- 物理参数（重心高度、下压力、轮胎曲线）用 ScriptableObject 外置，调手感不动代码。

### 6.2 3D 角色与场景

- **场景资源**：道路/路口/红绿灯/街景 Blockout → 精模替换；URP 中等画质。
- **角色模型**：“妹妹”3D 模型 + 骨架（对话气泡 + 表情 BlendShape + 头部朝向跟随镜头）。
- **表情/情绪**：4 档情绪映射到 BlendShape/贴图切换，由 `GameStateManager` 事件驱动。

### 6.3 音频管线

```
麦克风(48kHz) → Unity重采样16kHz → gRPC上行(20ms/块)
TTS下行24kHz PCM块 → AudioClip环形队列 → AudioSource(低延迟模式)
三总线混音：Voice(AI语音) / SFX(引擎环境) / BGM
         ↑ 说话时SFX/BGM自动ducking -6dB（保证语音清晰度）
```

- 上行采集用 `OnAudioFilterRead` 而非轮询 `Microphone.GetData`，避免丢帧。
- 回声消除：**播放 AI 语音时暂停上行采集**（半双工）。UI 明确显示“妹妹说话中…”状态。

### 6.4 GPU 显存/算力预算（16GB VRAM）

| 占用方 | 预算 | 说明 |
|--------|------|------|
| LLM Q4_K_M 7B | ~5.5GB（含 KV cache 4k ctx） | 伴生进程内 LLamaSharp Vulkan |
| TTS VITS / ASR（DirectML，轻量） | ~1GB | 可空闲卸载 |
| Unity 渲染 | ~3-4GB | URP + 中等画质 |
| 余量 | ~4GB | 安全垫 |

**争抢处理**：

1. Unity `Application.targetFrameRate=60` + 渲染与推理错峰（玩家说话时才推理）。
2. 若实测掉帧：LLM 降为 Q4_0，或减少 `GpuLayerCount` 将尾部层放 CPU。

### 6.5 延迟预算分解与达标手段

| 环节 | 目标 | 手段 | 实测（RX 9070 XT，热会话） |
|------|------|------|--------------------------|
| VAD 判停 | 150ms | 调优 silence 阈值 | 未接（P2 遗留） |
| ASR 尾包 | <200ms | 流式推理，判停即出 final | **45–90ms**（paraformer int8 / CPU） |
| LLM 首 token | <400ms | 全层 GPU offload；grammar 预编译 | **~150ms**（有状态会话复用 KV cache） |
| LLM 出 action 字段 | <600ms | action 排在 reply 之前生成 | **~230ms**（流式增量提取，action 只输出 intent） |
| TTS 首音频 | <350ms（实时）/ <50ms（语音库） | 分句流式合成；语音库命中率 >70% | **~0ms**（70 条语音库 100% 命中） |
| **端到端体感**（说完→车动） | **<1.2s** | | **~0.23s 热 / 0.68s 冷** ✅ |

> 关键实现约束（实测踩出来的，详见开发进度 §7-1）：`FlashAttention=false`（Vulkan 上开启慢 2.5 倍）、`useMemorymap=false`、有状态会话复用 KV cache、流式增量提取 action。

---

## 7. Prompt 工程规范

### 7.1 系统 Prompt 骨架

> 完整版另文档维护，纳入版本控制。

```
[身份] 你是林小雨，21岁，傲娇毒舌但车技稳，最烦指令不清不楚的人。
[规则1] 输出严格遵循JSON格式：{"action":{...},"tts_hint":...,"reply":...,"trust_delta":...}
[规则2] action.intent 只能从以下枚举选择：{枚举清单}
[规则3] reply≤40字，口语化，可用"哥""诶""啧"等语气词，禁止书面语。
[规则4] 指令模糊时 intent=QUESTION_BACK，不要猜。
[规则5] 涉及危险驾驶（闯红灯/逆行/撞人）时 intent=IGNORE，trust_delta=-5，并斥责。
[可用台词库] {高频台词清单，附voicelib://id，语义匹配时优先直接引用}
[当前状态] {GameState模板注入}
[记忆] {摘要 + 旧账队列}
```

### 7.2 工程化管理

- 所有 Prompt 存为模板文件（文本占位符渲染），**Git 版本控制，每次修改记录 AB 测试结论**。
- 建立**回归测试集** `dialogue_cases.{json,yaml}`，100+ case：标准指令 / 模糊指令 / 辱骂 / 危险指令 / 闲聊 / 旧账触发。每次 Prompt 或模型变更全量跑，LLM-as-judge + 人工抽检，三项指标：人格一致性、指令正确率、安全拦截率。
- 台词素材库（voice-corpus）：CSV 管理 `{id, text, emotion, intent_tags, level, priority}`，作为语音库生成与 Prompt 注入的单一数据源。

---

## 8. 测试与质量保障

> 测试顺序与「语音交互优先」对齐：**语音交互在接入 3D 前先行验收**。

| 层级 | 内容 | 工具/方式 | 阶段 |
|------|------|----------|------|
| 单元测试 | ActionParser 限幅/拦截、SafetyFilter、VAD 状态机、StateMachine | xUnit / NUnit，覆盖率 >80%（安全模块 100%） | 语音交互阶段 |
| **语音交互自测** | 无 Unity 命令行闭环：`--text` / `--wav` / `--mic` | `Copilot.AIService --cli`，脚本可复现 | 语音交互阶段 |
| 对话回归 | 100+ case 全量跑（§7.2） | CI 脚本，指标看板 | 语音交互阶段 |
| 延迟/WER 基线 | 各环节火焰图、噪声环境识别率 | 埋点 + 时间戳 | 语音交互阶段 |
| 集成测试 | Unity↔伴生进程断连恢复、超时降级、麦克风拔插 | 故障注入脚本 | 3D 接入阶段 |
| 3D 场景测试 | WheelCollider 手感、表情/动画联动、掉帧 | Unity Editor Play + Profiler | 3D 接入阶段 |
| 真机配置矩阵 | 9070XT / 7800XT / NVIDIA 对照 / 8GB 降级 | 每种记录延迟与帧率基线 | 发布阶段 |

**8GB 显存降级模式（必做）**：LLM 换 Qwen2.5-3B Q4 + TTS/ASR 按需加载 + Unity 纹理降档，保证中端 A 卡/N 卡玩家可玩。

---

## 9. 关键技术风险与验证方案

### T1：LLamaSharp + Vulkan 在 A 卡上的推理性能【语音交互阶段第一周必做】

```
验证项：
① LLamaSharp 后端包 Vulkan 在 RX 9070 XT（RDNA4）编译/运行
② Qwen2.5-7B Q4_K_M 全 offload：记录 prompt eval / token gen 速度
③ 伴生进程与 Unity 同时运行时帧率与显存占用

验收线：token gen ≥ 25 tok/s；Unity ≥55fps；显存总占用 < 14GB
失败路径：Vulkan → CPU → 降 3B 模型 → 纯语音库 + 意图分类架构
```

> **实测结论（2026-10-10，RX 9070 XT / b 系列 Vulkan build）**：
> - **必须关闭 FlashAttention**：开启时 **35 tok/s**，关闭后 **100 tok/s（2.5 倍）**。这是本方案最反直觉的一条，默认值已定为关。
> - **必须关闭 mmap**：Windows+Vulkan 下 mmap 会让 `token_embd.weight` 回退到 CPU，冷启动 prefill 1071ms → 652ms。
> - **必须用有状态会话**：无状态执行器每回合重算整段 prompt；换常驻 context 后热会话 prefill 1088ms → 154ms。
> - 结果：Qwen2.5-7B Q4_K_M 全 offload（29/29 层），显存 ~4.7GB，**单回合端到端 冷启 1.25s / 热 0.7–1.0s**，达标。
```

### T2：sherpa-onnx ASR/VAD/TTS 链路

```
① 流式 paraformer 中文识别 + Silero VAD 在噪声环境 WER
② VITS TTS 首包延迟

验收线：WER < 15%（安静）/ < 30%（游戏声，配合 ducking）；TTS 首包 < 500ms
不达标 → 上行加 RNNoise 降噪 / 提高语音库命中率至 85%
```

### T3：端到端流水线联调

```
语音交互阶段：伴生进程内麦克风→文本→动作JSON→TTS回放，火焰图定位瓶颈。
3D 阶段：Unity gRPC 联调，脚本模拟上/下行，记录全链路耗时。

验收线：说完→车动 < 1.2s（语音库路径）/ < 1.8s（实时TTS路径）
```

---

## 10. 构建与发布技术方案

```
1. 伴生进程：dotnet publish Copilot.AIService（self-contained，内嵌 .NET 运行时 + Native 库）
2. 模型权重：单独放 StreamingAssets/models/（约 5GB，随包分发或首启下载）
3. Unity：Build Windows x64 + 内置启动器脚本
   → 启动器先拉起 Copilot.AIService.exe（检测 50051 就绪）再进游戏
   → 游戏退出时优雅终止 AI 进程（避免残留占显存）
4. 首次运行自检：Ping 接口验证模型加载成功，失败弹诊断向导（自动检测 Vulkan/DirectML 可用性）
5. Steamworks：分包 depot（主程序 + 模型资产分开，支持增量更新）
```

**配置外置**：`appsettings.json` / `config.yaml` 暴露后端选择（vulkan/cpu）、量化等级、VAD 阈值、TTS 路由开关，玩家低配可自行降级，无需重编译。

---

## 11. 实施顺序（语音交互优先）

> 核心原则：**先做成可自测的语音交互，验收通过后再接入 3D 模型**。语音交互阶段不依赖 Unity 工程与美术资源，风险前置、尽早暴露。

### 阶段 A：环境与语音交互闭环

| 阶段 | 内容 | 依赖 |
|------|------|------|
| P0 | 环境搭建 + 共享项目骨架（Copilot.Contracts） | — |
| P1 | T1 验证：LLamaSharp + Vulkan 跑 Qwen2.5-7B | P0 |
| P2 | sherpa-onnx ASR + VAD 管线（T2 噪声/识别验证） | 可与 P1 并行 |
| P3 | 伴生进程 gRPC + LLamaSharp 封装 + GBNF grammar | P1 |
| P4 | **语音交互闭环**：伴生进程命令行（麦克风→文本→动作JSON→TTS回放），T3 验证 | P2、P3 |

### 阶段 B：语音交互测试验收

| 阶段 | 内容 | 依赖 |
|------|------|------|
| P5 | ActionParser/SafetyFilter/StateMachine 单元测试 + 覆盖率达标 | P4 |
| P6 | 对话回归 100+ case + 延迟/WER 基线 | P4 |
| P7 | **验收门禁**：语音交互全绿（<1.2s 端到端、WER 达标、安全拦截达标） | P5、P6 |

### 阶段 C：接入 Unity 3D

| 阶段 | 内容 | 依赖 |
|------|------|------|
| P8 | Unity 车辆场景 + ActionExecutor + WheelCollider 调参 | 可与 P5-P7 并行 |
| P9 | **接入 3D 模型**：角色模型/表情/动画 + 场景精模 | P8 |
| P10 | 双进程联调：语音开车闭环（语音库自动合成管线并入） | P7、P9 |
| P11 | 状态系统表现层（信任/情绪/旧账）+ 语音库批量生产 | P10 |
| P12 | 打包器/启动器/自检向导/降级模式 + 长稳 + 真机矩阵 → 冻结发布 | P7-P11 |

```
依赖图：
P0 ─→ P1 ─→ P3 ─→ P4 ─→ P5/P6 ─→ P7 ──────────→ P10 ─→ P11 ─→ P12
       P2 ──────┘              ↑                  ↑
                            P8 ─→ P9 ───────────┘
```

---

*文档结束 —— 修订请更新版本号并记录变更日志。*