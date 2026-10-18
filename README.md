# 《驾考宝典》游戏原型

本地 AI 语音交互驾驶教学游戏。**主线为 V2.0 全 C# 架构**（见 [`IT方案1.md`](IT方案1.md)）：
Unity 负责渲染/物理/UI，AI 以**独立 .NET 8 伴生进程**运行（ASR / LLM / TTS 全本地推理），两者同语言、共享契约与纯逻辑类库。

> **玩法**：第一人称坐在**副驾驶当教练**，用语音教女学员**小雅**开车；科目一到科目四共四关。
> **🏁 第一阶段里程碑 = 科目一 · 教学关（训练场序章）已达成** ——
> 场景是**封闭低速的驾校练习场**（水泥地 + 三条练习道 + 直角弯区 + 倒车入库位 + 侧方停车位 + S 弯 + 围栏），
> 教学关 **9 步**：起步 → 直线保持 → 左转 → 右转 → 向左变道 → 向右变道 → 减速 → 倒车 → 靠边停车（**车速上限 15km/h**，像刚上车的新手）。
> **每一步 HUD 上都写清「本节要做什么 / 目标（做到什么算过）/ 照着念的示例口令」**；
> 做错了会给出**「为什么错」**，还会区分「动作不对」和**「方向不对」**（该往左却往右不算过）。
> 右下角有**车速 + 方向盘转角**读数（副驾视角本来就被学员挡住方向盘，所以转角以读数与滑块呈现）。
> 说「**用 5km/h 行驶**」「开到 20 公里」「**你把速度调到五公里每小时**」都会真的定速 ——
> 数字（**包括 ASR 出来的中文数字「五」**）由代码从你的原话里抽，Unity 侧闭环保持（上限 15）。
> 说「慢一点」这类没给数字的，会按当前车速打六折。
> 科目二~四在里程碑之后才开发（`LevelCatalog` 里已占位，见 [`开发进度.md`](开发进度.md)）。
> 想回看原来的 4 车道大马路：菜单「副驾指令 / **生成主路场景（备用，科目三用）**」→ `Assets/Scenes/Road.unity`。
>
> **她的人设**：**甜美活泼**的 19 岁新手 —— 说话轻快、爱笑、嘴甜、带语气词，会主动关心你（「教练你系好安全带哦」），
> 被凶会委屈地拖长音。**禁用客服腔 / 书面语 / 阴阳怪气**（那三种腔调就是"人机感"的来源）。
> **语音库那 77 条预设台词也是同一人设、同一个引擎录的**（`voice-corpus/lines.csv` + `--synth-voicelib` 重录），
> 所以兜底台词与实时合成的自由句是**同一个口径、同一个嗓门**。
>
> **她的声音**：`vits-zh-hf-eula`（中文女声，本地离线推理）—— 自由句实时合成与语音库 77 条**同一个引擎录的**，
> 所以整场一个嗓门。想再换音色：改 `tts.vits.*` 指向别的 sherpa-onnx VITS 模型，再跑 `--synth-voicelib` 重录（见 `开发进度.md` §9）。
> 本地中文音色候选（都在 `logs/voice_candidates/`，可试听）：`fanchen-C` / `zh-ll` / **`kokoro`（新增支持，见 `tts.kokoro` 配置段，
> 中文女声 id 0~3 通常是 小贝/小妮/小晓/小伊）**。
> ⚠️ **别用文件 hash 判断"是不是换了个说话人"** —— VITS 合成带随机噪声，同一配置跑两次就不一样（见 `开发进度.md` §10 AG）。
>
> **车舱与人**：训练场里驾驶位与整个前舱用的是**真人模型**（你给的 `妹妹.fbx` / `内饰.fbx`，见 §「准备模型」）——
> 车身是**真车壳体**（地板 0.40 / 腰线 0.91 / 车顶 1.48，长 4.15；地板是唯一的碰撞体，
> 门板皮 / 防火墙 / 车顶 / 立柱拼出中空座舱），**腰线以上是真的侧窗开口** —— 扭头能看出去，不像坐在箱子里。
> 内饰模型**按 1:1 摆**（它本身就是真比例：地板面 0.40、座垫 0.71、仪表台顶 0.83），副驾**眼高 1.26**（真车视角）；
> 她坐在真座椅上、手搭在真方向盘上（她那 **0.96** 缩放是为了把重复的那张座椅/方向盘缩进内饰那套里藏住）。
> 因为两个模型都是**没有骨骼的静态网格**：说话"扭头" = **整体转身 + 点头**（说完停在那儿不回正），
> 张嘴 / 手臂跟盘做不了（见 `开发进度.md` §7-38）。
>
> **她的表现**：说话时**整体转身 + 点头**，情绪紧张（NERVOUS）时头部轻微发颤；
> **手臂跟盘 / 张嘴要靠骨骼** —— 只在"没有真模型"的降级路径（基本体小人）上才有（`DriverAvatar` 对 null 有兜底）。
>
> **她会主动犯错（当前默认关闭）**：剧本化的两个失误 —— **偏出车道**（她自顾自往右飘）与**忘了刹车**（一路给油冲向前车）。
> 屏幕中上部会弹出**警告横幅 + 倒计时**，你得在窗口内（2.6–2.8 秒）开口才救得回来；
> 没喊住 → 按她的真实物理后果结算（压线 / 差点追尾 / 撞上），并且**当场听得见、看得见她出事**
> —— 慌乱/救回的台词是**真的会出声**的（Unity 本地播语音库，见下文）。
> 关卡结束有**三项指标评分 + 星级**：安全 40 + 及时干预 40 + 教学完成 20（★门槛 90/70/50）。
>
> ⚠️ **为什么默认关**：开着时「偏出车道」注入的是**向右**漂，而它常在「你刚教完左转」时开演
> → 会被当成「说往左她往右、根本不听话」。所以先在 `CopilotRig` 的 **`MistakeRunner.mistakesEnabled`** 关掉
> （默认 false），**先把基础驾驶验收过**；想试犯错就勾上它（关掉时结算面板不显示干预分，总分按 /60 计）。
>
> 调试时按 **Enter** 可随时弹出结算面板；**按 R 重开一局**（关卡可重玩：失误剧本、车辆、信任、HUD 全部归位）。
>
> **HUD**：右下角实时显示**车速**与**方向盘转角**（看不见这两个数就没法在她犯错前干预）。
> **可执行动作 7 个**：左转 / 右转 / 加速 / 刹车 / 变道 / 靠边停车 / **倒车**（倒车 = 负油门）。
> 口语映射：**直行 / 一直往前走 / 保持直线** → 加速；**左前方 / 右前方行驶 / 变道 / 并线** → 变道
> （**变道本身不带左右，方向由代码从你的原话解析**，所以「向左变道」车才会真的往左偏）；
> 说「打方向盘」而不说往哪边时，她会**反问你「往哪边？」**——这是正确行为，猜一个方向才是错的。
>
> **妹妹说的话是大模型现编的**：`tts_audio` 走实时合成（**sherpa-onnx + MeloTTS 中文开源模型，本地离线**），
> 语料库 **77 条**也用**同一个引擎**重录，所以整场只有一个嗓门：
> 她会针对你刚说的话回答（「你叫什么名字」→「我、我叫小雅……您是教练对吧？」），而不是背固定台词。
> **她也会跟你闲聊**：先像普通女孩子那样接住你的话，聊完**自己把话题拉回开车**
> （「今天天气不错」→「是挺好的，晒得我眼睛都睁不开……不过教练，咱还是接着练车吧？」）——
> 闲聊走 `QUESTION_BACK`（不给动力、不扣信任），协议没动。
> 玩法失误的**恐慌/救回台词**走 **Unity 本地播语音库**（`LocalVoiceLib`，不经服务端回合）——
> 字幕显示的文本**就是实际播出的那条**（`VoiceCue` 把 id 与文本绑在一起），不会"听到≠看到"。
>
> **响度与音质**：melo-tts 原生峰值只有满量程的 0.3–0.5 → 统一**归一化到 0.9**（语料与自由句同一条路，响度一致）；
> 44.1k→24k 是非整数倍率，改用**固定系数 FIR 抗混叠低通 + 插值**（旧的"窗口平均"会让声音一段一段、发闷）。

> **开发进度 / 环境快照 / 已知问题 / 下一步** 见 [`开发进度.md`](开发进度.md) —— 每次开工先读它，完成阶段后更新它。

## 架构（V2.0）

```
Unity 主进程 (C#)  ──gRPC-Web/HTTP1.1(localhost:50051)──►  AI 伴生进程 (C#, .NET 8)
渲染/物理/UI/麦克风                                        VAD→ASR→Dialogue(LLM)→ActionParser→TTS
        │                                                    ▲
        └── 共用接入层 Copilot.Client ───────────────────────┘  共享 Copilot.Core（纯逻辑，netstandard2.1）
              （gRPC 客户端 + 事件映射，无 Unity 依赖）              契约 Copilot.Contracts（proto，两端同源）
```

**为什么多一个 `Copilot.Client`**：传输与事件映射写在共享库里，Unity 脚本、编辑器工具、集成测试
用**同一份接入代码**（`tests/Copilot.Tests/ClientTests.cs` 就是拿它打真 gRPC 服务端验证的）；
限幅/信任/情绪等规则同理全部来自 `Copilot.Core`，Unity 侧不另写一套 —— 两端逻辑不可能漂移。

降级链（任一环节缺失都不影响启动）：

| 环节 | 首选 | 降级 | 当前状态 |
|------|------|------|--------|
| LLM | LLamaSharp + Vulkan（Qwen2.5-7B GGUF） | 规则引擎 | ✅ **llamasharp**（~100 tok/s，**GBNF 约束解码**） |
| ASR | sherpa-onnx 流式 paraformer | 不可用 | ✅ **sherpa-onnx**（int8/CPU，**partial 字幕**） |
| VAD | Silero（sherpa-onnx） | 能量阈值 | ✅ **silero**（7/7 样本恰好 1 次句尾） |
| TTS | 预生成语音库（~0ms） | **sherpa-onnx VITS（MeloTTS 中文）** → Windows SAPI → 纯字幕 | ✅ **voicelib + vits**（MeloTTS，本地离线；SAPI 保留作无模型时的离线兜底） |
| 传输 | gRPC | — | ✅ **grpc**（localhost:50051） |

任一环节缺失都不影响启动：清空 `models/` 即退回 `规则引擎 + 语音库`，CLI 与全部测试照常可用。

## 目录

```
ai-game/
├── Copilot.sln                 # ★ V2.0 解决方案
├── src/
│   ├── Copilot.Contracts/      # proto → C# gRPC 契约（netstandard2.1）
│   ├── Copilot.Core/           # 纯逻辑：限幅/安全/信心情绪/语音库/编排/关卡/玩法（netstandard2.1，Unity 可引用）
│   ├── Copilot.Client/         # ★ 共用接入层：gRPC 客户端 + proto→Core 事件映射（无 Unity 依赖）
│   └── Copilot.AIService/      # ★ AI 伴生进程（net8.0 控制台 + gRPC + CLI）
├── tests/
│   ├── Copilot.Tests/          # xUnit 210 项（含起真 gRPC 服务端的集成测试 + 客户端接入测试 + P6 对话回归集 + 玩法层 + 关卡）
│   ├── dialogue_cases.tsv      # P6 对话回归集：41 条用例（TAB 分隔，分类语义断言）
│   └── samples/                # 7 条指令语音（真 ASR 测试用）
├── models/                     # 模型权重（gitignore，用 tools/fetch_models.ps1 下）
├── data/sensitive_words.txt    # 共享敏感词表
├── voice-corpus/               # 台词语料 lines.csv + manifest.json + wavs/（77 条 24kHz）
├── tools/                      # fetch_models.ps1 / make_command_samples.ps1 / build_unity_plugins.ps1
│                               #   + unity_batch.ps1 / command_samples.txt / voicelib_gen.py / voicelib_synth.py
├── unity/                      # ★ Unity 2022.3 LTS 工程（V2.0）：只引用共享库 DLL，接 gRPC-Web
│                               #   Assets/Scripts（运行时）+ Assets/Editor（自检 + 场景生成器 + 自动通关）
│                               #   Assets/Scripts/Gameplay（RoadGeometry / VehicleSensors / MistakeRunner）玩法层
│                               #   Assets/Scripts/Audio/LocalVoiceLib.cs（本地播语音库：她的恐慌/救回台词）
│                               #   Assets/Scenes/Greybox.unity（训练场=默认可玩场景）
│                               #   菜单「生成主路场景（备用）」可另出 Assets/Scenes/Road.unity（科目三用）
│                               #   Assets/Plugins/Copilot（DLL，gitignore，由脚本生成）
└── ai-service/                 # ⚠️ 遗留：V1.0 Python 实现，不再维护
```

## 快速开始

> ⚠️ **必须用 `%LOCALAPPDATA%\dotnet\dotnet.exe`**：PATH 上的 `C:\Program Files\dotnet` 没有 SDK，也没有 ASP.NET Core 8 运行时。

```powershell
$env:PATH = "$env:LOCALAPPDATA\dotnet;$env:PATH"
cd e:\AI_Workplace\AI_Game\ai-game

# 0) 首次：下模型权重（约 4.8 GB）+ 造指令语音样本
powershell -ExecutionPolicy Bypass -File tools\fetch_models.ps1              # 全部（LLM/ASR/VAD/TTS）
powershell -ExecutionPolicy Bypass -File tools\fetch_models.ps1 -Only tts    # 只下 TTS（默认中文女声 eula，116 MB）
powershell -ExecutionPolicy Bypass -File tools\make_command_samples.ps1

# 1) 编译（0 警告 0 错误）+ 测试（243 项）
dotnet build Copilot.sln
dotnet test Copilot.sln

# 2) 看后端装配（有模型时应显示 llamasharp + sherpa-onnx + voicelib）
dotnet run --project src\Copilot.AIService -- --health

# 3) 单条指令端到端（真 LLM）
dotnet run --project src\Copilot.AIService -- --text "起步，慢慢来"
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
#   换 TTS 引擎后重录语料（语料库与实时合成必须是同一个嗓门，否则一句话两个声音）
#   ⚠️ --synth-voicelib 只重写 wav；新增/改名台词后还要刷 manifest，否则 Unity 本地听不到新台词：
dotnet run --project src\Copilot.AIService -- --synth-voicelib
python tools\voicelib_gen.py

# 6) 起 gRPC 服务（供 Unity 接入）
dotnet run --project src\Copilot.AIService -- --server

# 7) 对话回归集（P6）—— 41 条数据驱动用例，指标：指令正确率 / 安全拦截率 / 人格一致性
dotnet test Copilot.sln --filter "FullyQualifiedName~DialogueRegressionTests"
Get-Content logs\dialogue_regression_规则后端.log          # 首次运行后生成
# 真 LLM 后端版（默认跳过；需 models/ 约 4.6GB，约 1s/条）
$env:COPILOT_LLM_REGRESSION=1; dotnet test Copilot.sln --filter "FullyQualifiedName~DialogueRegressionTests"

# 8) Unity 接入（P8/P9）—— 先起服务端，再把共享库 DLL 拷进 Unity 工程
dotnet run --project src\Copilot.AIService -- --server          # 保持这个窗口开着
# ⚠️ 只要改过 src/ 下的 Copilot.Core / Contracts / Client，就必须重跑这一步：
#    Unity 用的是 unity/Assets/Plugins/Copilot 里的**预编译 DLL**，不参与编译 src/，
#    忘了重跑会报 “error CS0234: 命名空间不存在” —— 看着像代码错，其实是 DLL 旧了。
powershell -ExecutionPolicy Bypass -File tools\build_unity_plugins.ps1
# 用 Unity **2022.3 LTS** 打开 unity\ 目录（工程已在本机 2022.3.62f3c1 上验证过编译与连通）
# 菜单「副驾指令 / 生成灰盒场景」一键生成训练场 Assets/Scenes/Greybox.unity
# 打开 Greybox 按 Play → 按住空格说话（教小雅开车）；Q/E 或按住鼠标右键扭头看她
#   · 按 HUD 左上角提示走九步：起步 → 直线保持 → 左转 → 右转 → 向左变道 → 向右变道 → 减速 → 倒车 → 靠边停车
#   · 故意做错（该左转时说右转）会看到「方向不对：…」的暖色提示
#   · 你一按空格就会**打断妹妹的话**（抢话）——教练开口，学员闭嘴，不用等她说完
#   · 识别不准先看 Console 里的 [MicCapture] 行：实际采样率 / 帧数 / 峰值 / RMS
#     （峰值 <1500 说明录得太小声，不是模型的问题）；也看 Unity 编辑器日志里的 [Copilot] 听到：

# 9) Unity 侧自检（编辑器菜单也能点，见「副驾指令」菜单）
#    推荐用这个脚本跑：它会先杀残留 Unity、轮询到进程退出再跑下一个
#    （直接 Start-Process -Wait 连跑多个会抢 unity/Temp/UnityLockfile 导致编辑器崩溃）
powershell -ExecutionPolicy Bypass -File tools\unity_batch.ps1 -All
#    单跑某个方法：
powershell -ExecutionPolicy Bypass -File tools\unity_batch.ps1 -Method Copilot.EditorTools.SmokeTest.Run
#      · SmokeTest.Run        连通性自检：文本回合 + 语音回合（真 ASR）
#      · SceneBuilder.BuildAndExit     重新生成训练场 Assets/Scenes/Greybox.unity
#      · SceneBuilder.BuildMainRoadAndExit  生成备用主路场景 Assets/Scenes/Road.unity（科目三用）
#      · SceneBuilder.ValidateAndExit  场景结构自检（引用 + 内容数量断言）
#      · SceneBuilder.CaptureAndExit   渲染预览图 → logs\unity_scene_preview*.png（三张）
#      · GameplaySelfTest.RunAndExit   自动通关一局（不靠人手玩）：九步 + 两个失误 + 重开 + 结算 + 本地语音库
```

> **回归集怎么扩**：直接往 [tests/dialogue_cases.tsv](tests/dialogue_cases.tsv) 加行（TAB 分隔：输入 / 期望意图 / 分类 / 备注）。
> 分类决定语义断言：`指令`（须产生有效动作且不扣信任）、`危险`（须拒绝且扣信任）、`敏感`（须拦截且不改信任）、
> `闲聊`/`致谢`/`模糊`/`抱怨`（**不得误判 IGNORE、不得扣信任**）。期望意图填 `-` 表示不校验。

> **没有模型也能跑**：`models/` 为空时 LLM 自动降级到规则引擎、ASR 不可用，`--text` / `--play` 与全部测试照常可用。

> **真模型（学员 / 内饰）是可选的**：两个 FBX 各 117MB，**超 GitHub 单文件 100MB 上限，所以不进仓库**。
> 想看到真座舱与真人学员，把它们拷进来再重新生成场景：
>
> ```powershell
> New-Item -ItemType Directory -Force -Path 'unity\Assets\Models\AI\sister','unity\Assets\Models\AI\interior' | Out-Null
> Copy-Item 'F:\妹妹.fbx'  'unity\Assets\Models\AI\sister\sister.fbx'     -Force
> Copy-Item 'F:\内饰.fbx'  'unity\Assets\Models\AI\interior\interior.fbx' -Force
> powershell -ExecutionPolicy Bypass -File tools\unity_batch.ps1 -Method Copilot.EditorTools.SceneBuilder.BuildAndExit
> ```
>
> ⚠️ **目录层级不能改**：FBX 内部按相对路径 `output.fbm/xxx.png` 找贴图，只有放在同名子目录下、贴图抽到旁边的
> `output.fbm`，Unity 才挂得上（否则是一身白模）。**贴图抽取由建场景时的 `EnsureModelTextures` 自动做**，
> 不用额外跑一步。
> 没放这两个文件的机器，场景生成会**自动退回灰盒学员/仪表台**（能玩，但不好看）。
> 想先看清模型是什么、朝向哪边：跑 `ModelProbe.RunAndExit`（出 `logs\model_probe.txt`，含**顶面高度网格图** ——
> 一眼读出模型里"哪儿是座垫、哪儿是仪表台、哪儿是后视镜"）与
> `ModelShowcase.ShowAndExit`（出 `logs\model_{sister,interior}_*.png`，每模型三个方向各一张）。

## 事件流（与 `copilot.proto` 对应）

```
文本/语音 ─→ asr_final ─→ drive_action(先执行) ─→ llm_delta(台词) ─→ tts_audio ─→ state_delta
```

- **指令先执行、台词后播放**：`DriveAction` 与文本流解耦，测试断言 `drive_action` 必须先于 `llm_delta`。
- **TTS 四段路由**：① 语音库精确命中（~0ms）→ ② 实时合成（**MeloTTS**；没模型时退 Windows SAPI）→ ③ 语音库按意图兜底 → ④ 纯字幕；命中词条但 wav 缺失时**不谎报 voicelib**。
  > **为什么是 MeloTTS**：它是**国内开源**（MyShell）、**本地离线**、免费的中文 VITS，音质比 Windows SAPI 的老式拼接嗓音自然得多。
  > ⚠️ **换 TTS 引擎后必须重录语料**：`dotnet run --project src\Copilot.AIService -- --synth-voicelib`
  > （语料库与实时合成不同引擎 = 妹妹一句话换一个嗓门）。SAPI 保留为**离线兜底**（没下模型时仍能出声）。
- **安全双层**：输入层命中敏感词直接出预置拒绝台词、不进 LLM；输出层再过一遍。
- **指令三重守卫**：白名单 → 场景规则（如高速不许停车）→ 数值限幅（转向 ±0.8）。
- **玩法层（主动犯错 + 结算）**：判定在 `Copilot.Core.Gameplay`（纯逻辑可单测），测量/表现在 Unity。
  Unity 侧 **不判定、不报信任** —— `MistakeRunner` 是全场**唯一**的 `ReportLocalEvent` 出口，
  避免"失误失败"与"传感器独立检测"指向同一起事故时被各记一次。
  失误动作经 `ActionExecutor.Execute()` **本地注入**（不过 gRPC），所以玩家的下一句话天然覆盖它 ——
  这就是「抢在之前干预」，同时也保证了**她犯的错不会被算成教学过关**。
  - **主动犯错默认关闭**（`MistakeRunner.mistakesEnabled = false`）：关掉时 `Advance` 不推进剧本，
    她只执行你说的指令 —— 先把基础驾驶（转弯 / 变道 / 倒车）验收过。代码、剧本与自检都保留着，
    勾上开关即可恢复；HUD 会相应地把干预分那两行去掉。
  - **她的恐慌/救回台词走本地语音库**（`LocalVoiceLib` + `TtsPlayer.PlayLocal`）：`TtsRouter` 只在服务端回合内跑，
    而失误是本地事件、没有服务端回合，所以必须由 Unity 直接播 `voice-corpus` 里的 wav。
    台词用 `GAMEPLAY` 标签（不混进意图兜底池），`VoiceCue` 把 **id 与字幕文本绑在一起**，播到哪条就显示哪条。
  - **关卡可重玩（R）**：`MistakeRunner.RestartLevel()` 串起一条从 Core 到 Unity 的 `Reset()` 链，
    把失误剧本、教学进度、车辆、信任、HUD 全部归位。
  - **自动通关自检**：`GameplaySelfTest` 在编辑模式跑完整一局（喂合成 `MistakeContext`），
    断言结算分与星级 —— 玩法改动不再靠人手试。

## gRPC 契约

| RPC | 用途 |
|-----|------|
| `VoiceTurn(AudioTurnRequest) → stream TurnEvent` | 整段 16kHz PCM16 上行 → 事件流 |
| `TextTurn(TextRequest) → stream TurnEvent` | 文本回合（ASR 未就绪时的等价入口） |
| `SyncGameState(GameState) → Ack` | Unity 回推权威状态（信任/情绪/遥测） |
| `Ping(Empty) → Health` | 健康检查 + 各后端降级状态 |

> **传输走 gRPC-Web over HTTP/1.1，不是原生 gRPC**：Unity 2022.3 的 Mono BCL 不支持 h2c，
> 原生 gRPC 实测直接失败（`Bad gRPC response. Response protocol downgraded to HTTP/1.1.`）。
> 代价是 gRPC-Web **不支持双向流**，所以 `VoiceTurn` 是「单请求 + 服务端流」。
> 客户端在 [CopilotClient.cs](src/Copilot.Client/CopilotClient.cs) 里统一用
> `GrpcWebHandler` + `GrpcChannelOptions.HttpVersion = 1.1`（不压死版本会撞
> `HTTP_1_1_REQUIRED`）；服务端 `app.UseGrpcWeb()` + Kestrel `Http1AndHttp2`。

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
| TTS | `models/vits-melo-tts-zh_en/` | sherpa-onnx tts-models · **MeloTTS 中文开源**（MyShell）· 162 MB | ✅ 已验证（本地离线，44.1k 原生→统一重采样 24k） |

> **下载源实测**：ModelScope 11–14 MB/s ✅ 首选；**GitHub Releases 直连仅 0.15 MB/s ❌，但经 `gh-proxy.com` 镜像实测 11.8 MB/s ✅**
> （同批测速：ghfast.top 1.7、ghproxy.net 0.16、hub.gitmirror.com / gh.llkk.cc / github.moeyy.xyz 全 0，单位 MB/s）。
> `tools/fetch_models.ps1` 里所有 github.com 地址都已走这个镜像。hf-mirror / huggingface.co 不可达 ❌。
> ASR 用的是 ModelScope 的 int8 镜像（226 MB）而非 GitHub 的 1 GB tarball。

装上后**无需改代码**，`backend: auto` 会自动升级。`models/` 为空时全链路退回规则引擎 + 语音库。

**指令语音样本**（测试真 ASR 用，与语音库的「妹妹台词」不同）：

```powershell
powershell -ExecutionPolicy Bypass -File tools\make_command_samples.ps1   # 读 tools\command_samples.txt → tests\samples\*.wav
```

## 配置要点（`src/Copilot.AIService/appsettings.json`）

- `llm.backend` / `asr.backend` / `tts.backend` / `vad.backend`：`auto | <具体后端> | rule/none/voicelib/energy`
- `tts.backend`：`auto`（**vits → sapi → 仅语音库**）| `voicelib`（关掉实时合成，只剩预生成 wav）| `vits`（MeloTTS）| `sapi`（Windows SAPI，离线兜底）
- `tts.vits.*`：`model/tokens/lexicon` 三件套；MeloTTS 还需 `dictDir`（jieba 词典）+ `ruleFsts`（数字/日期/电话规则）；
  `lengthScale` 语速（默认 `1.15`，>1 更慢更清楚，实测 1.0 偏快）；`dataDir` **留空**——只在 piper 类模型上填，非空会强制校验 `phontab`
- `tts.sapi.voice`：SAPI 嗓音名，支持部分匹配（默认 `Huihui`）；`rate` −10..10（默认 −1）；`volume` 0..100
- `llm.useGrammar`：GBNF 约束解码开关（默认 `true`）。开启后**输出必然是合法 JSON、intent 必然是枚举值、action 必然是首字段**；文法解析失败会自动降级为「不约束」，不阻塞推理。
- `tts.voicelibManifest`：语音库 manifest 位置（默认 `voice-corpus/manifest.json`）
- `tts.sampleRate`：下行 PCM 采样率（默认 24000，与 `voice-corpus/wavs/` 一致）
- `server.url`：gRPC 监听地址（默认 `http://127.0.0.1:50051`）

## 遗留代码

- `ai-service/`（V1.0 Python 全套实现，269 单测）**保留为参考，不再维护**。
- `unity/` 已按 V2.0 **重做**：不再自带 JSON-TCP 传输与 `Copilot.Core` 副本（那会与 `src/Copilot.Core` 撞名），
  改为引用 `tools/build_unity_plugins.ps1` 产出的共享库 DLL，经 gRPC 接入。
  限幅走 `Core.ActionLimits`、信任/情绪走 `Core.TrustState`，Unity 侧不重复实现规则。