# 《副驾指令》IT 实施方案（技术开发版）

> **项目代号**：副驾指令（暂定）
> **文档版本**：V1.0
> **更新日期**：2026-09-27
> **适用范围**：本地 AI 语音交互驾驶模拟游戏全技术栈
> **目标环境**：Windows 11 + AMD RX 9070 XT (16GB VRAM)

---

## 目录

1. [系统总体架构](#1-系统总体架构)
2. [环境与依赖清单](#2-环境与依赖清单)
3. [接口协议设计](#3-接口协议设计核心契约)
4. [AI 服务实现方案](#4-ai-服务实现方案)
5. [Unity 侧实现方案](#5-unity-侧实现方案)
6. [Prompt 工程规范](#6-prompt-工程规范)
7. [关键技术风险与验证方案](#7-关键技术风险与验证方案)
8. [测试与质量保障](#8-测试与质量保障)
9. [构建与发布技术方案](#9-构建与发布技术方案)
10. [实施顺序](#10-实施顺序纯技术依赖序)

---

## 1. 系统总体架构

### 1.1 架构原则

- **双进程分离**：Unity（渲染/物理/UI）与 AI 服务（Python）独立进程，通过本地 RPC 通信，互不阻塞、可独立重启调试。
- **全本地推理**：ASR / LLM / TTS 全部本地运行，零云端依赖，零数据外发。
- **流水线并行**：ASR→LLM→TTS 采用流式 pipeline，各环节重叠执行而非串行等待。
- **降级兜底**：每个 AI 环节都有失败降级路径（实时 TTS 失败→预生成语音库；LLM 输出非法→规则引擎兜底）。

### 1.2 架构图

```
┌─────────────────────────── Unity 主进程 (C#) ───────────────────────────┐
│                                                                        │
│  InputLayer          GameCore                    PresentationLayer     │
│  ┌─────────┐    ┌──────────────────┐    ┌────────────────────────┐     │
│  │麦克风采集 │    │ VehicleController │    │ UIManager(对话气泡/表情) │     │
│  │(Unity    │    │ (WheelCollider×4) │    │ AudioMixer(3总线)      │     │
│  │ Audio)   │    ├──────────────────┤    │ CameraDirector        │     │
│  └────┬────┘    │ GameStateManager  │    └────────────────────────┘     │
│       │         │ (信任/情绪/评分)   │                 ↑                │
│       ↓         ├──────────────────┤                 │                │
│  ┌─────────┐    │ ActionExecutor    │─────────────────┘                │
│  │AudioBus │    │ (指令→物理层映射  │                                   │
│  │(PCM推流)│    │  +限幅+插值)     │                                   │
│  └────┬────┘    └────────▲─────────┘                                   │
│       │                  │                                             │
│  ┌────▼──────────────────┴────┐                                        │
│  │      AIClient (gRPC)       │  ← 含断线重连/超时/心跳                  │
│  └────────────┬───────────────┘                                        │
└───────────────┼────────────────────────────────────────────────────────┘
                │ localhost:50051 (gRPC/protobuf, 含流式接口)
┌───────────────▼──────────────── Python AI服务进程 ───────────────────────┐
│                                                                        │
│  ┌──────────┐   ┌──────────┐   ┌───────────────┐   ┌──────────────┐   │
│  │ VAD      │──→│ ASR      │──→│ DialogueCore   │──→│ ActionParser │   │
│  │ Silero   │   │ FunASR   │   │ (Prompt组装/    │   │ (JSON Schema │   │
│  │ (断句)    │   │ streaming │   │  记忆管理/     │   │  校验+白名单) │   │
│  └──────────┘   └──────────┘   │  LlamaCpp调用) │   └──────┬───────┘   │
│                                └───────────────┘          │           │
│                 ┌─────────────────────────────┐           ↓           │
│                 │ SafetyFilter (敏感词+规则)    │──→┌──────────────┐    │
│                 └─────────────────────────────┘   │ TTSRouter    │    │
│                                                    │ ①语音库检索   │    │
│                                                    │ ②GPT-SoVITS  │    │
│                                                    │   实时合成    │    │
│                                                    └──────────────┘    │
└────────────────────────────────────────────────────────────────────────┘
                GPU: ROCm / Vulkan —— 16GB VRAM 显存预算分配（见 §5.4）

```

---

## 2. 环境与依赖清单

### 2.1 系统环境

| 组件 | 版本要求 | 备注 |
|------|---------|------|
| Windows 11 | 23H2+ | 或 Linux（开发期可双轨） |
| AMD Adrenalin 驱动 | 最新 WHQL | RDNA4 需确认 HIP SDK 兼容版本 |
| ROCm on Windows | HIP SDK 6.2+ | 若 RDNA4 (gfx1201) 支持不全 → 走 Vulkan |
| Python | 3.10（固定） | GPT-SoVITS / FunASR 对 3.10 兼容最好，**禁用 3.11+** |
| Unity | 2022.3 LTS + URP 14 | |
| Git | LFS 启用 | 模型权重/音频资产走 LFS |

### 2.2 AI 组件版本锁定

| 组件 | 选型 | 版本/量化 |
|------|------|----------|
| LLM | Qwen2.5-7B-Instruct-GGUF | Q4_K_M（首选）/ Q5_K_M（质量兜底） |
| LLM 运行时 | llama.cpp | ROCm backend；**备选 Vulkan backend** |
| ASR | FunASR paraformer-zh-streaming | ONNX 导出，onnxruntime-directml 或 CPU 推理（模型小，CPU 足够） |
| VAD | Silero VAD v5 | ONNX，CPU |
| TTS | GPT-SoVITS v2 | 训练后导出推理模型；备选 CosyVoice2 |
| RPC | grpcio + protobuf | 流式接口用 `stream` 类型 |
| 音频格式 | PCM 16kHz mono（ASR 入）/ 24kHz mono（TTS 出） | 全链路统一重采样 |

### 2.3 目录结构规范

```
project/
├── unity/                      # Unity工程
│   └── Assets/
│       ├── Scripts/
│       │   ├── AI/             # AIClient, gRPC生成代码, DTO
│       │   ├── Vehicle/        # VehicleController, ActionExecutor
│       │   ├── State/          # GameStateManager, TrustSystem
│       │   ├── Audio/          # MicCapture, AudioBusRouter
│       │   └── UI/             # DialogueBubble, EmotionFace
│       └── StreamingAssets/
│           └── voicelib/       # 预生成语音库(.wav + manifest.json)
├── ai-service/                 # Python服务
│   ├── proto/                  # .proto定义
│   ├── core/
│   │   ├── vad.py
│   │   ├── asr.py
│   │   ├── dialogue.py         # Prompt组装/记忆管理
│   │   ├── llm.py              # llama.cpp封装
│   │   ├── action_parser.py
│   │   ├── safety.py
│   │   └── tts/
│   │       ├── router.py
│   │       ├── voicelib.py     # 语音库检索
│   │       └── gpt_sovits.py   # 实时合成
│   ├── models/                 # GGUF/ONNX/SoVITS权重
│   ├── server.py               # gRPC入口
│   └── tests/
├── voice-corpus/               # 台词语料库(csv+json)
└── tools/                      # 语音库批量生成脚本等

```

---

## 3. 接口协议设计（核心契约）

### 3.1 Protobuf 定义

```protobuf
syntax = "proto3";
package copilot;

service AIService {
  // 上行音频流 → 下行完整AI响应流（双向流式）
  rpc VoiceTurn(stream AudioChunk) returns (stream TurnEvent);
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
  bytes pcm16 = 1;      // 24kHz mono
  bool from_voicelib = 2;  // true=预生成库, false=实时合成
}

```

### 3.2 关键设计说明

| 决策 | 理由 |
|------|------|
| **双向流式而非请求-响应** | ASR 中间结果可实时上屏字幕；TTS 音频分块边合成边播，首音延迟大幅降低 |
| **DriveAction 独立于文本流** | 驾驶指令在 LLM 生成前段 token 时即可提取（见 §4.4），指令**先执行**、台词**后播放**，符合"妹妹边说边打方向"的真实感 |
| **obstacle_summary 用枚举拼接而非自由文本** | 避免 Unity 侧序列化复杂场景；也防止 LLM 幻觉编造不存在的路况 |
| **StateDelta 由 AI 服务计算后回推** | 信任值变化逻辑集中在 Prompt/规则层，Unity 只做表现 |

---

## 4. AI 服务实现方案

### 4.1 VAD + ASR 管线

```python
# 核心流程（伪代码）
vad = SileroVAD(threshold=0.5, min_silence_ms=600)  # 静音600ms判定说完
asr = FunASRStreaming(model="paraformer-zh-streaming")

async def voice_turn(chunk_iter):
    buffer = RingBuffer(sample_rate=16000)
    for chunk in chunk_iter:
        buffer.write(chunk.pcm16)
        if vad.is_speech(chunk): speech_started = True
        # 流式喂ASR，每收到partial就yield给Unity做实时字幕
        for partial in asr.feed(chunk):
            yield TurnEvent(asr_partial=partial)
        if speech_started and vad.silence_exceeded(buffer, 600):
            break
    final_text = asr.finalize()
    yield TurnEvent(asr_final=final_text)

```

**要点**：

- 麦克风采集在 Unity 侧完成（`Microphone.Start`，480 帧环形缓冲），重采样到 16kHz 后推流，避免 Python 侧管理音频设备的跨平台问题。
- ASR 置信度 < 0.6 时，`asr_final` 附带低置信标记，DialogueCore 触发"反问"分支（"啊？你说啥？风太大没听清"）——技术缺陷转化为玩法。
- VAD 参数需在实际噪音环境下实测调优，预留配置热重载。

### 4.2 LLM 封装（llama.cpp）

**方案 A（首选）：llama-cpp-python + ROCm**

```python
from llama_cpp import Llama
llm = Llama(
    model_path="models/qwen2.5-7b-instruct-q4_k_m.gguf",
    n_ctx=4096, n_gpu_layers=99,        # 全量offload到GPU
    n_threads=8, verbose=False,
    logits_processor=[JsonSchemaConstraint(ACTION_SCHEMA)]  # 语法约束
)

```

**方案 B（ROCm 不可用时）：llama.cpp 独立 server + Vulkan 后端**

```bash
llama-server -m qwen2.5-7b-instruct-q4_k_m.gguf \
  --port 8080 -ngl 99 -c 4096 --parallel 1 \
  -b 512 -fa on                        # flash attention省显存

```

Python 侧改用 OpenAI 兼容 HTTP 客户端。**两方案接口层完全一致（`LLMBackend` 抽象基类），可一键切换。**

**结构化输出强制**：不依赖 Prompt 自觉，用 **GBNF grammar / JSON Schema 约束解码**，从根上保证输出可解析：

```
root ::= "{\"action\":" action ",\"tts_hint\":" hint ",\"reply\":\"" text "\",\"trust_delta\":" int "}"
action ::= "{\"intent\":\"" intent-enum "\",\"steering\":" num ",\"throttle\":" num ",\"brake\":" num ",\"hold_ms\":" int "}"
intent-enum ::= "TURN_LEFT" | "TURN_RIGHT" | "ACCELERATE" | "BRAKE" | "LANE_CHANGE" | "PARK" | "IGNORE" | "QUESTION_BACK"
hint ::= "null" | "\"voicelib://" path "\""
num ::= "-"? digit ("." digit digit)?

```

> 注意：grammar 中 **action 字段排在 reply 之前生成**——保证 Unity 最先拿到驾驶指令。

### 4.3 对话上下文管理（记忆系统）

```
上下文窗口预算 (n_ctx=4096):
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

- 摘要压缩由同一 LLM 执行（异步、在玩家非说话间隙触发），Prompt："将以下对话压缩为 50 字内要点，保留玩家犯过的错误和承诺"。
- "旧账"写入条件：`trust_delta <= -5` 或玩家辱骂事件 → 存入 SQLite（`memory.db`，按 save_slot 分区），跨关卡持久化。
- 系统 Prompt 中人格描述与 GameState 分离注入，GameState 模板化：`"当前车速{speed}km/h，{obstacles}。你的情绪：{emotion}。你对哥哥的信任：{trust}/100。"`

### 4.4 动作解析与安全层

```python
def parse_and_guard(llm_output: dict, game_state: GameState) -> DriveAction:
    # 1. Schema已由约束解码保证，此处做值域校验
    action = DriveAction(**llm_output["action"])
    # 2. 物理限幅（LLM可能输出1.5的转向）
    action.steering = clamp(action.steering, -0.8, 0.8)
    action.throttle = clamp(action.throttle, 0, 1)
    # 3. 危险指令拦截：白名单外intent → 强制IGNORE + 反问话术
    if action.intent not in ALLOWED_INTENTS:
        return fallback_question_back(game_state)
    # 4. 场景规则校验：高速行驶中 intent=PARK → 拒绝执行，
    #    DialogueCore生成"哥你疯啦现在100码你让我靠边？"
    if violates_scene_rules(action, game_state):
        return refuse_with_line(action, game_state)
    return action

```

**SafetyFilter 双层设计**：

- **输入层**：玩家 ASR 文本过敏感词库（AC 自动机，<1ms）→ 命中则不进入 LLM，直接返回预置拒绝台词。
- **输出层**：LLM reply 文本过同一词库 + 正则兜底 → 命中则替换为该情绪档的通用吐槽台词（语音库必有）。

### 4.5 TTS 双路由策略（延迟核心优化）

| 路由 | 适用场景 | 延迟 | 实现 |
|------|---------|------|------|
| **① 预生成语音库** | 高频固定台词：动作确认、吐槽（800-1200 条）、教学提示、拒绝话术 | **~0ms**（本地 wav 检索） | `manifest.json` 索引：`{text, wav_path, emotion, intent_tags[], embedding}`；检索 = 精确匹配→关键词匹配→语义向量匹配（top1，阈值 0.75） |
| **② 实时合成** | 自由闲聊、含动态信息的台词（"现在都{speed}码了你让我{intent}？"） | 首块 300-500ms | GPT-SoVITS 流式分句合成：LLM 每生成完整一句即触发合成，与后续 token 生成并行 |

**关键机制**：LLM 输出含 `tts_hint` 字段（`voicelib://xxx` 或 null）——Prompt 引导 LLM 在语义匹配语音库时直接引用库内台词（语音库文本清单注入系统 Prompt），匹配不上才自由发挥走实时 TTS。**目标：70% 以上回复命中语音库。**

---

## 5. Unity 侧实现方案

### 5.1 车辆控制层

- 基于 **WheelCollider** 标准方案（前轮 steerAngle、后轮 motorTorque、四轮 brakeTorque）。
- **AI 指令不直接写 Collider**，经 `ActionExecutor` 中间层：

```csharp
// 指令平滑化：避免LLM离散指令导致车辆抖动
public class ActionExecutor {
    DriveAction _current; float _elapsed;
    void FixedUpdate() {  // 50Hz
        _elapsed += Time.fixedDeltaTime * 1000;
        float t = Mathf.Clamp01(_elapsed / _current.hold_ms);
        float steer = Mathf.SmoothStep(0, _current.steering * MAX_STEER, t);
        _vehicle.ApplyInput(steer, _current.throttle, _current.brake);
    }
    public void Execute(DriveAction a) { _current = ClampAction(a); _elapsed = 0; }
}

```

- 物理参数（重心高度、下压力、轮胎曲线）用 ScriptableObject 外置，调手感不动代码。

### 5.2 状态系统

- `GameStateManager`：信任值/情绪/评分的唯一数据源，**事件驱动**（C# event），UI 订阅刷新。
- 信任值变化来源两路：AI 回推的 `StateDelta`（对话行为）+ Unity 本地判定（实际撞车/闯红灯/压线，物理层最可信）。
- 情绪为 4 档有限状态机，档位变化触发：表情 UI 切换 + BGM 混音参数 + 回推 AI 侧影响后续台词风格。
- 关键事件（罢工/旧账触发）写入 `memory.db`（Unity 侧用 sqlite-net）与 AI 服务共享。

### 5.3 音频管线

```
麦克风(48kHz) → Unity重采样16kHz → gRPC上行(20ms/块)
TTS下行24kHz PCM块 → AudioClip环形队列 → AudioSource(低延迟模式)
三总线混音：Voice(AI语音) / SFX(引擎环境) / BGM
         ↑ 说话时SFX/BGM自动ducking -6dB（保证语音清晰度）

```

- 上行采集用 `OnAudioFilterRead` 而非轮询 `Microphone.GetData`，避免丢帧。
- 回声消除：**播放 AI 语音时暂停上行采集**（半双工），规避 Windows 端 AEC 依赖。UI 明确显示"妹妹说话中…"状态。

### 5.4 GPU 显存/算力预算（16GB VRAM）

| 占用方 | 预算 | 说明 |
|--------|------|------|
| LLM Q4_K_M 7B | ~5.5GB（含 KV cache 4k ctx） | `-fa on` 可再省 |
| GPT-SoVITS | ~2GB | 空闲时可卸载，按需加载（闲聊关卡才驻留） |
| ASR/VAD（ONNX/CPU） | 0（CPU） | 不吃显存 |
| Unity 渲染 | ~3-4GB | URP + 中等画质 |
| 余量 | ~4GB | 安全垫 |

**争抢处理**：

1. Unity `Application.targetFrameRate=60` + 渲染与推理错峰（玩家说话时才推理，开车画面时推理已完成）。
2. 若实测掉帧：LLM 降为 Q4_0，或减少 `n_gpu_layers` 将尾部层放 CPU（7B 模型留 4-6 层 CPU 对速度影响可接受）。

### 5.5 延迟预算分解与达标手段

| 环节 | 目标 | 手段 |
|------|------|------|
| VAD 判停 | 150ms | 调优 silence 阈值至体感自然 |
| ASR 尾包 | <200ms | 流式推理，判停即出 final |
| LLM 首 token | <400ms | 全层 GPU offload；grammar 预编译 |
| LLM 出 action 字段 | <600ms | grammar 中 action 排在 reply 之前生成 |
| TTS 首音频 | <350ms（实时）/ <50ms（语音库） | 分句流式合成；语音库命中率 >70% |
| **端到端体感**（说完→车动） | **<1.2s** | |

---

## 6. Prompt 工程规范

### 6.1 系统 Prompt 骨架

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

### 6.2 工程化管理

- 所有 Prompt 存为 `.md` 模板文件（Jinja2 渲染），**Git 版本控制，每次修改记录 AB 测试结论**。
- 建立**回归测试集**：`tests/dialogue_cases.yaml`，100+ case 覆盖：标准指令 / 模糊指令 / 辱骂 / 危险指令 / 闲聊 / 旧账触发。每次 Prompt 或模型变更全量跑，LLM-as-judge + 人工抽检评分，三项指标：
  - 人格一致性得分
  - 指令正确率
  - 安全拦截率
- 台词素材库（voice-corpus）：CSV 管理 `{id, text, emotion, intent_tags, level, priority}`，作为语音库生成与 Prompt 注入的单一数据源。

---

## 7. 关键技术风险与验证方案

### T1：ROCm/RDNA4 上 llama.cpp 性能验证【第 1 周必须完成】

```
验证项：
① HIP SDK 对 gfx1201 的支持 → 编译ROCm后端跑通
② Qwen2.5-7B Q4_K_M 全offload: 记录 prompt eval / token gen 速度
③ 与Unity同时运行时的帧率与显存占用

验收线：token gen ≥ 25 tok/s；Unity侧不低于55fps；显存总占用 < 14GB
失败路径：ROCm → Vulkan后端 → Ollama(vulkan) → 3B模型 → 纯语音库+意图分类架构

```

### T2：GPT-SoVITS 实时合成延迟

```
① 训练"妹妹"音色（3-5min授权样本，2-3小时训练）
② 实测分句流式合成首包延迟

验收线：首包 < 500ms
不达标 → 降低输出采样率 / 预热模型常驻 / 提高语音库命中率至85%

```

### T3：FunASR 噪声环境识别率

```
① 采集实际游戏环境音频（引擎声+BGM+说话）测试WER

验收线：WER < 15%（安静）、< 30%（游戏声环境，配合ducking）
不达标 → 上行音频加降噪(RNNoise ONNX) / 调整ducking深度

```

### T4：端到端流水线联调

```
双进程gRPC联调，用脚本模拟Unity端，记录全链路各环节耗时火焰图。

验收线：说完→车动 < 1.2s（语音库路径）/ < 1.8s（实时TTS路径）

```

---

## 8. 测试与质量保障

| 层级 | 内容 | 工具/方式 |
|------|------|----------|
| 单元测试 | ActionParser 限幅/拦截逻辑、SafetyFilter、VAD 状态机 | pytest，覆盖率 >80%（安全相关模块 100%） |
| 对话回归 | 100+ case 全量跑（§6.2） | CI 脚本，指标看板 |
| 性能测试 | 各环节延迟火焰图、8 小时长稳（显存泄漏/句柄泄漏） | 自研 profiler 埋点 + gRPC metadata 带时间戳 |
| 集成测试 | Unity↔AI 断连恢复、超时降级、麦克风拔插 | 故障注入脚本 |
| 真机配置矩阵 | 9070XT / 7800XT / NVIDIA 对照 / 8GB 显存降级模式 | 每种配置记录延迟与帧率基线 |

**8GB 显存降级模式（必做）**：LLM 换 Qwen2.5-3B Q4 + GPT-SoVITS 按需加载 + Unity 纹理降档，保证中端 A 卡/N 卡玩家可玩。

---

## 9. 构建与发布技术方案

```
打包流程（CI脚本一键完成）：
1. Python侧: PyInstaller打包ai-service.exe（内嵌依赖，排除模型权重）
2. 模型权重: 单独放入 StreamingAssets/models/（约6GB，随包分发）
3. Unity: Build Windows x64 + 内置启动器脚本
   → 启动器先拉起 ai-service.exe（检测50051端口就绪）再进游戏
   → 游戏退出时优雅终止AI进程（避免残留占显存）
4. 首次运行自检: Ping接口验证模型加载成功，失败弹诊断向导
   （自动检测HIP/Vulkan可用性，引导切换后端）
5. Steamworks: 分包 depot（主程序 + 模型资产分开，支持增量更新）

```

**配置外置**：`config.yaml` 暴露后端选择（rocm/vulkan/cpu）、量化等级、VAD 阈值、TTS 路由开关，玩家低配可自行降级，无需重编译。

---

## 10. 实施顺序（纯技术依赖序）

| 阶段 | 内容 | 依赖 |
|------|------|------|
| S1 | 环境搭建 + T1 验证（ROCm/Vulkan 跑通 Qwen2.5） | — （一切的地基） |
| S2 | FunASR + Silero VAD 管线 + T3 验证 | 可与 S1 并行 |
| S3 | GPT-SoVITS 音色训练 + T2 验证 | 可与 S1 并行 |
| S4 | gRPC 协议实现 + llama.cpp 封装 + 约束解码 grammar | S1 |
| S5 | 命令行端到端原型（麦克风→文本→动作JSON→TTS播放）+ T4 验证 | S2、S3、S4 |
| S6 | Unity 驾驶场景 + ActionExecutor + 车辆物理调参 | 可与 S5 并行 |
| S7 | 双进程联调：语音开车闭环 | S5、S6 |
| S8 | 状态系统（信任/情绪/旧账 SQLite）+ SafetyFilter | S7 |
| S9 | 语音库批量生产管线（tools/voicelib_gen.py + GPT-SoVITS 批处理） | S3 |
| S10 | Prompt 回归测试集 + CI | S8 |
| S11 | 打包器/启动器/自检向导/降级模式 | S7-S9 |
| S12 | 长稳测试 + 真机矩阵测试 → 冻结发布 | 全部 |

```
依赖图：
S1 ─┬─→ S4 ─→ S5 ─┬─→ S7 ─→ S8 ─→ S10
S2 ─┤             │         ↑
S3 ─┴─────────────┘    S6 ──┘
S3 ──→ S9 ──────────────┘
S7~S10 ──→ S11 ──→ S12

```

---

*文档结束 —— 修订请更新版本号并记录变更日志。*