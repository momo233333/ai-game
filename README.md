# 《副驾指令》游戏原型

基于 `IT方案1.md` 落地的第一版：**Python AI 服务可运行骨架**（方案 S4–S5）。
零强制第三方依赖，缺什么就降级到什么，保证随时能跑通「玩家指令 → 驾驶动作 + 妹妹台词 + 信任/情绪变化」的完整闭环。

> **开发进度 / 环境快照 / 已知问题 / 下一步** 见 [`开发进度.md`](开发进度.md) —— 每次开工先读它，完成阶段后更新它。

## 目录

```
AI_Game/
├── IT方案1.md              # 原始 IT 实施方案
├── 开发进度.md             # ★ 阶段进度、验证基线、技术债、下一步（每次会话先读）
├── ai-service/             # AI 服务（Python，双进程架构的服务端）
│   ├── proto/copilot.proto # gRPC 契约（方案 §3.1 原样实现）
│   ├── config.yaml         # 后端选择 / 阈值 / 降级开关（方案 §9 配置外置）
│   ├── core/
│   │   ├── types.py        # GameState / DriveAction / TurnEvent / Emotion
│   │   ├── state.py        # 信任值、4 档情绪 FSM、Unity 本地事件
│   │   ├── safety.py       # SafetyFilter：AC 自动机敏感词 + 情绪档拒绝话术
│   │   ├── action_parser.py# 值域限幅 / intent 白名单 / 场景规则拦截
│   │   ├── llm.py          # LLMBackend：llama_cpp / openai / rule（降级）
│   │   ├── dialogue.py     # Prompt 组装 + 记忆系统（滑动窗口/摘要/旧账 SQLite）
│   │   ├── corpus.py       # 台词语料检索（intent+emotion+关键词）
│   │   ├── vad.py          # Silero VAD（可选）/ 能量 VAD（兜底）
│   │   ├── asr.py          # FunASR（可选）/ Vosk（可选）/ 文本模式（兜底）
│   │   ├── audio.py        # 16k 重采样 / wav 切块（语音测试用）
│   │   ├── mic.py          # 麦克风采集（sounddevice，可选）
│   │   ├── ptt.py          # 按键说话：Ctrl 全局监听（ctypes 低级键盘钩子）
│   │   └── tts/            # 双路由：语音库检索 / GPT-SoVITS 实时合成（可选）
│   ├── pipeline.py         # 流式编排：事件顺序 = 动作 → 台词 → 状态
│   ├── server.py           # 服务端：gRPC（生成后）/ JSON-TCP 回退
│   ├── cli.py              # 命令行端到端原型（本地 / 远端 / --mic / --wav）
│   ├── data/sensitive_words.txt
│   ├── models/             # vosk 模型解压位置（模型需自行下载）
│   └── tests/              # 208 项单元测试（含 4 项真模型冒烟）+ 44 项对话回归 + samples/ 语音样本
├── unity/                  # Unity 2022.3 工程（阶段 S6 骨架）
│   ├── Assets/Scripts/
│   │   ├── Core/           # ★ 纯 C#（无 UnityEngine）：JSON-TCP 会话、TurnEvent、
│   │   │                   #   ActionExecutor 数学、信任/情绪表现层、PCM 处理
│   │   ├── AI/AIClient.cs  # MonoBehaviour 封装：后台线程收发 → 主线程分发
│   │   ├── Vehicle/        # VehicleController(WheelCollider) + ActionExecutor 中间层
│   │   ├── State/          # GameStateManager（信任/情绪/遥测同步）
│   │   ├── Audio/          # MicCapture(PTT 上行) + TtsPlayer(下行) + ducking
│   │   ├── UI/             # 对话气泡打字机 / 表情 / HUD / 文本调试输入
│   │   └── CopilotBootstrap.cs # 一键装配，挂一个组件即可跑闭环
│   └── Tests~/             # 无头测试（Unity 忽略 ~ 结尾目录）
│       ├── CoreTests/      # Core 单测 + 起真 python server 的端到端联调
│       └── UnityStubs/     # UnityEngine 桩：Assets/Scripts 全量编译检查
├── voice-corpus/
│   ├── lines.csv           # 台词语料（单一数据源：id/text/emotion/intent_tags/priority）
│   └── manifest.json       # 语音库索引（voicelib_gen.py 生成）
└── tools/voicelib_gen.py   # 语音库批量生成脚本
```

## 快速开始

```bash
cd AI_Game

# 1) 生成语音库索引（CSV → manifest.json）
python tools/voicelib_gen.py

# 2) 启动真 LLM（可选，不启动则自动降级到规则引擎）
& ai-service\models\llama\llama-server.exe `
  -m ai-service\models\qwen2.5-7b-instruct-q4_k_m-00001-of-00002.gguf `
  --host 127.0.0.1 --port 8080 -ngl 99 -c 4096 --jinja

# 3) 单条指令端到端（本地管线）
cd ai-service
python cli.py --text "前面路口左转"

# 4) 交互模式
python cli.py
你> 往左打方向
你> /event collision        # Unity 侧本地事件：撞车 -12 信任
你> /state
你> /quit

# 5) 起服务（JSON-TCP，50051）+ 远端客户端
python server.py
python cli.py --server 127.0.0.1:50051 --text "靠边停车"

# 6) 测试
python -m unittest discover -s tests      # 208 项单元测试（llama-server 不在线时 4 项 skip）
python tests/run_dialogue_regression.py   # 44 项对话回归（方案 §6.2，固定 rule 后端）

# 6) 覆盖率（需 pip install coverage）
python -m coverage run --source=core,pipeline,server,cli,config -m unittest discover -s tests
python -m coverage run --append --source=core,pipeline,server,cli,config tests/run_dialogue_regression.py
python -m coverage report -m

# 7) 语音交流测试（见下节）
python tools/make_speech_samples.py       # 用 Windows 中文 TTS 生成语音样本
python cli.py --wav tests\samples\left_turn.wav   # 音频 → 识别 → 动作闭环
python cli.py --mic                       # 麦克风实时对话（说话→停顿半秒出结果）

# 8) Unity 侧测试（需 .NET SDK 8，仓库根目录执行）
dotnet run --project "unity/Tests~/CoreTests/CoreTests.csproj" -- --require-server   # 23 项：Core 单测 + 起真服务联调
dotnet build "unity/Tests~/UnityStubs/UnityCompileCheck.csproj"                      # Assets/Scripts 对桩编译
```

## 事件流（与方案 §3.1 对应）

```
玩家输入 ─→ asr_final ─→ drive_action(先执行) ─→ llm_delta*(打字机) ─→ tts_audio ─→ state_delta
```

- **指令先执行、台词后播放**：`DriveAction` 在 LLM 输出的首个字段产出，与文本流解耦。
- **TTS 双路由**：先查 `voicelib://` 精确/语义命中（~0ms），未命中走实时合成（未装则降级为字幕 + 静默）。
- **安全双层**：输入层敏感词命中直接返回预置拒绝台词，不进 LLM；输出层再过一遍词库。

## 降级矩阵（当前骨架全部生效）

| 环节 | 首选 | 降级 | 当前状态 |
|------|------|------|---------|
| LLM | llama.cpp (Qwen2.5 GGUF) | OpenAI 兼容 HTTP → **规则引擎** | **openai**（llama-server:8080，实测 108 tok/s） |
| ASR | FunASR paraformer streaming | **Vosk 中文小模型** → 文本指令模式 | text（模型下载中） |
| VAD | Silero VAD ONNX | 能量阈值 VAD | energy/none |
| TTS | GPT-SoVITS 实时合成 | 预生成语音库 → 字幕静默 | voicelib |
| 传输 | gRPC/protobuf | JSON-TCP（同事件结构） | **grpc**（`--transport json` 可回退） |

装上依赖后无需改代码，`config.yaml` 里 `backend: auto` 会自动升级；也可显式指定并强制报错。

## 开启 gRPC（方案正式传输层，**当前已接通**）

```bash
pip install grpcio grpcio-tools
cd ai-service
# 注意：-I 必须是仓库根（.），否则生成的 _grpc 文件用裸 import copilot_pb2，包内导入会失败
python -m grpc_tools.protoc -I . --python_out=. --grpc_python_out=. proto/copilot.proto
python server.py --transport grpc   # 生成的 copilot_pb2*.py 位于 proto/ 下
```

三个 RPC：`VoiceTurn`（音频上行 → 事件流）、`TextTurn`（ASR 未装时的文本回合，事件结构与 JSON-TCP 的 `text_turn` 完全一致）、`SyncGameState` / `Ping`。

## 配置要点（config.yaml）

- `llm.backend`: `auto | llama_cpp | openai | rule`
- `vad.backend` / `asr.backend` / `server.transport`: 同理
- `memory.*`: 滑动窗口、旧账阈值（`trust_delta <= -5` 写入 SQLite，按 `save_slot` 分区）
- `tts.voicelib_path`: 语音库 manifest 位置

## 语音交流测试（怎么测“说话”）

**分三档，从零依赖到真麦克风：**

```bash
cd ai-service

# 档1：文本对话（0 依赖）— 测对话逻辑/规则/情绪
python cli.py
你> 前面路口左转

# 档2：音频回放（需 Vosk 模型）— 测 ASR→动作 整条链路，可复现
python tools/make_speech_samples.py          # 生成 tests/samples/*.wav（Windows 中文 TTS）
python cli.py --wav tests\samples\left_turn.wav

# 档3：麦克风按键说话（需 Vosk 模型 + sounddevice）
python cli.py --mic                       # 按住 Ctrl 说话，松开自动识别发送，Ctrl+C 退出
python cli.py --mic --free                # 免提模式：说完停顿约 0.6s 自动结束
```

**准备 ASR（二选一）：**
- Vosk（轻量推荐）：下载 `vosk-model-small-cn-0.22.zip`（约 42MB）解压到 `ai-service/models/vosk-model-small-cn-0.22/`
- FunASR（方案 S2 完整版）：`pip install funasr`（需拉 torch，方案建议 Python 3.10）

**预期输出（每一轮）：**

```
  [字幕] 转弯吧   ← 说话时实时 partial 字幕
  [玩家] 向左转弯            ← asr_final
  [动作] ↰左转 steering=-0.60 throttle=0.30 brake=0.00 hold=1200ms
行，左转交给我                ← 妹妹台词（llm_delta 打字机）
  [TTS ] (语音库) 行，左转交给我
  [状态] 信任 61/100 (+1) 情绪=CALM
```

**验证要点：** ① 字幕在说话过程中滚动 ② 松开 Ctrl（或免提模式停顿 0.6s）后 1 秒内出动作 ③ 危险指令（`speed_100.wav` 无速度同步时）走正常路径、同步速度后 `--event` 可触发拦截 ④ `swear.wav` 应被输入层敏感词拦截（不进 LLM）。


## Unity 侧（阶段 S6 骨架已落地）

工程在 `unity/`（Unity 2022.3 LTS + URP 可选），代码分两层：

- **`Assets/Scripts/Core/`**：纯 C#，零 UnityEngine 依赖 —— JSON-TCP 会话（断线重连/请求串行化）、
  `TurnEvent` 解析、`ActionExecutorCore` 限幅+平滑（方案 §5.1）、`StateView` 信任/情绪（与 `core/state.py`
  同规则同事件表）、PCM16 解码/重采样。**这层有独立测试，不装 Unity 也能验。**
- **`Assets/Scripts/{AI,Vehicle,State,Audio,UI}/`**：MonoBehaviour 薄封装，只做引用装配与主线程分发。

**接入步骤**：场景里放一辆带 `WheelCollider×4` 的车 → 挂 `CopilotBootstrap` 并拖入 `VehicleController`
→ 启动 `python ai-service/server.py`（默认 50051）→ Play。文本框（`TextInputSender`）可直接发指令，
按住 `V` 走麦克风上行（需 ASR 就位）。

```
文本/语音 → AIClient → ActionExecutor(drive_action, 先动) → VehicleController
                     ↘ TtsPlayer(tts_audio 字幕+播放) → DialogueBubble
                     ↘ GameStateManager(state_delta) → 信任条/表情
```

**无头测试（CI 也跑这套）**：

```bash
dotnet run --project "unity/Tests~/CoreTests/CoreTests.csproj" -- --require-server  # Core 单测 + 起真服务端到端
dotnet run --project "unity/Tests~/CoreTests/CoreTests.csproj" -- --no-server       # 只跑纯逻辑
dotnet run --project "unity/Tests~/CoreTests/CoreTests.csproj" -- --filter Executor # 按名字过滤
dotnet build "unity/Tests~/UnityStubs/UnityCompileCheck.csproj"                     # MonoBehaviour 层对桩编译
```

**CI**：`.github/workflows/ci.yml` —— push/PR 自动跑 Python 单测 + 对话回归 + 覆盖率、
Unity 脚本编译检查、Core 测试（起真 `server.py`，服务不可用即红）。

