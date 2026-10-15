using System;
using Copilot.AI;
using Copilot.Audio;
using Copilot.Core;
using Copilot.Core.Gameplay;
using Copilot.Core.Levels;
using Copilot.State;
using Copilot.UI;
using Copilot.Vehicle;
using UnityEngine;

namespace Copilot.Gameplay
{
    /// <summary>
    /// 玩法层的**唯一负责人**（挂 <c>CopilotRig</c>）：驱动剧本失误、把玩家干预对上窗口、
    /// 并把所有信任副作用收成一条出口。
    ///
    /// 它做的四件事：
    /// 1. 每帧组 <see cref="MistakeContext"/> 喂给 <see cref="MistakeDirector"/>（判定全在 Core）；
    /// 2. 开演时把小雅做的错事**注入 <see cref="ActionExecutor"/>**（本地调用，不经服务端）
    ///    —— 于是玩家下一句 `drive_action` 天然覆盖它，这就是「抢在之前干预」的机制；
    /// 3. 只订阅 <c>connection.TurnEventReceived</c> 来认「玩家说的动作」，
    ///    **绝不用 <c>ActionExecutor.ActionReceived</c>**（注入自己也会触发它）；
    /// 4. 把结果记进 <see cref="LevelSession"/> 并上报 <c>GameStateManager.ReportLocalEvent</c>。
    ///
    /// 规则：失误动作**绝不经过 <c>connection</c>** —— 那条路会走到 <c>TutorialDirector</c>，
    /// 让"她犯的错"被算成"教学过关"。
    /// </summary>
    [DefaultExecutionOrder(50)]   // 必须晚于 CopilotConnection.Update() 的事件派发
    public sealed class MistakeRunner : MonoBehaviour
    {
        public CopilotConnection connection;
        public ActionExecutor executor;
        public GameStateManager gameState;
        public VehicleController vehicle;
        public VehicleSensors sensors;
        public TutorialDirector tutorial;
        public DialogueBubble bubble;
        public GameplayHud gameplayHud;

        [Tooltip("本地语音库（她把慌乱/松口气说出来）；拿不到就只用字幕，见 LocalVoiceLib")]
        public LocalVoiceLib localVoice;
        [Tooltip("播放本地语音用的播放器（与妹妹说话同一条队列）")]
        public TtsPlayer ttsPlayer;

        [Header("主动犯错（第一阶段先关掉，只练基础驾驶）")]
        [Tooltip("关掉后小雅**不主动犯错**，只老老实实执行你说的指令；打开才演剧本失误（偏出车道/忘了刹车）。\n"
                 + "默认关闭：实测开着时，玩家刚教完左转她就往右飘，会被当成「说往左她往右、根本不听话」。")]
        public bool mistakesEnabled = false;

        [Header("剧本")]
        [Tooltip("两次失误之间至少隔这么久（秒）")]
        public float cooldownSeconds = 8f;

        [Tooltip("把「前方那辆车」摆在车头前多少米")]
        public float hazardSpawnDistanceM = 35f;

        [Tooltip("开演时是否让小雅先说一句（恐慌台词）")]
        public bool announceOnBubble = true;

        [Tooltip("同一个本地事件在这个时间窗内只记一次（避免同一起事故被两边各记一次）")]
        public float duplicateWindowSeconds = 2f;

        MistakeDirector _director;
        LevelSession _session;
        Transform _hazardCar;
        /// <summary>当前/刚结案的那个失误定义 —— 结案时要按它取"松口气/懊恼"那句。</summary>
        MistakeDefinition _lastDef;

        Vector3 _lastPosition;
        float _distanceM;
        bool _turnStartedAfterWarning;

        string _lastReportedEvent = string.Empty;
        float _lastReportedAt = -99f;

        CopilotConnection _boundConnection;
        VehicleSensors _boundSensors;

        /// <summary>主动犯错是否开着（HUD 据此决定要不要显示「干预分」那一行）。</summary>
        public bool MistakesEnabled
        {
            get { return mistakesEnabled; }
        }

        public MistakePhase Phase
        {
            get
            {
                EnsureInitialized();
                return _director.Phase;
            }
        }

        /// <summary>当前正在等干预的失误（Idle 时为 null）。</summary>
        public MistakeDefinition Current
        {
            get
            {
                EnsureInitialized();
                return _director.Current;
            }
        }

        public float RemainingMs
        {
            get
            {
                EnsureInitialized();
                return _director.RemainingMs;
            }
        }

        public LevelSession Session
        {
            get
            {
                EnsureInitialized();
                return _session;
            }
        }

        /// <summary>失误开演（HUD 弹横幅用）。</summary>
        public event Action<MistakeDefinition> MistakeOpened;

        /// <summary>失误结案（HUD 闪绿/闪红用）。</summary>
        public event Action<MistakeOutcome> MistakeResolved;

        void Awake()
        {
            EnsureInitialized();
        }

        /// <summary>
        /// 懒初始化：**不依赖 <c>Awake</c> 的调用时机**。
        ///
        /// 实测教训：在编辑器里给物体 <c>AddComponent</c> **不保证触发 `Awake`**
        /// （自动化自检就是在编辑模式下驱动这个组件的，结果 `Session` 是 null 直接空引用）。
        /// 所以状态对象按需创建，而不是只在 `Awake` 里建 —— 与 <see cref="TutorialDirector.Progress"/> 同一处理。
        /// </summary>
        void EnsureInitialized()
        {
            if (_director != null) return;

            var level = LevelCatalog.Subject1Tutorial;
            _session = new LevelSession(level.Id, level.Steps.Count);
            _director = new MistakeDirector(MistakeCatalog.Subject1Tutorial, cooldownSeconds);
            _director.Resolved += HandleResolved;
            _lastPosition = transform.position;
        }

        void OnEnable()
        {
            Rebind();
        }

        void Start()
        {
            Rebind();
        }

        void OnDisable()
        {
            BindConnection(null);
            BindSensors(null);
        }

        /// <summary>幂等绑定（AddComponent 先于字段注入触发 OnEnable，装配方注入后再调一次）。</summary>
        public void Rebind()
        {
            BindConnection(connection);
            BindSensors(sensors);
            if (sensors != null) _hazardCar = sensors.hazardCar;
            SetHazardActive(false);
        }

        void BindConnection(CopilotConnection target)
        {
            if (_boundConnection == target) return;
            if (_boundConnection != null) _boundConnection.TurnEventReceived -= OnTurnEvent;
            _boundConnection = target;
            if (_boundConnection != null) _boundConnection.TurnEventReceived += OnTurnEvent;
        }

        void BindSensors(VehicleSensors target)
        {
            if (_boundSensors == target) return;
            if (_boundSensors != null)
            {
                _boundSensors.CollisionDetected -= OnCollisionDetected;
                _boundSensors.LaneDeparted -= OnLaneDeparted;
            }
            _boundSensors = target;
            if (_boundSensors != null)
            {
                _boundSensors.CollisionDetected += OnCollisionDetected;
                _boundSensors.LaneDeparted += OnLaneDeparted;
            }
        }

        void Update()
        {
            EnsureInitialized();
            TrackDistance();
            Advance(Time.deltaTime * 1000f, BuildContext());

            if (tutorial != null)
            {
                _session.ObserveStepAdvance(tutorial.Progress.StepIndex);
            }
        }

        /// <summary>
        /// 推进一帧（<see cref="Update"/> 内部就是调它）。开演 → 注入 → 返回本帧结果。
        /// **公开出来是为了自动化自检/调试能手动喂合成上下文**（编辑模式下 Update 不跑）。
        /// </summary>
        public MistakeTick Advance(float dtMs, MistakeContext ctx)
        {
            EnsureInitialized();

            // 主动犯错关掉时，剧本**不推进**（教学进度照常由 Update 观测）。
            // 这样小雅只执行玩家说的指令 —— 第一阶段要的是「说左就左、说倒车就倒车」能对上。
            if (!mistakesEnabled) return default;

            var tick = _director.Tick(dtMs, ctx);

            if (tick.Opened && _director.Current != null) OnOpened(_director.Current);

            if (tick.InjectNow && tick.InjectAction != null && executor != null)
            {
                // 本地注入：不过 connection，所以不会走到 TutorialDirector（失误不算教学过关）
                executor.Execute(tick.InjectAction);
            }

            return tick;
        }

        /// <summary>
        /// 玩家这一轮对话**在窗口打开之后才开始**，并且产出了这个动作。
        /// 自检用它模拟玩家及时干预；事件流路径由 <see cref="OnTurnEvent"/> 判定后调它。
        /// </summary>
        public bool SubmitPlayerIntent(string intent)
        {
            EnsureInitialized();
            return _director.ObservePlayerIntent(intent);
        }

        /// <summary>
        /// 重开一局：把玩法状态、教学进度、车、信任、HUD 全部复位。
        /// 顺序有讲究 —— 先清玩法状态再归位车辆，最后收 HUD。
        /// </summary>
        public void RestartLevel()
        {
            EnsureInitialized();

            _director.Reset();
            _session.Reset();

            _distanceM = 0f;
            _lastPosition = transform.position;
            _turnStartedAfterWarning = false;
            _lastReportedEvent = string.Empty;
            _lastReportedAt = -99f;
            SetHazardActive(false);

            if (tutorial != null) tutorial.ResetProgress();
            if (executor != null) executor.Reset();
            if (vehicle != null) vehicle.ResetToStart();
            if (gameState != null) gameState.ResetTrust();
            if (gameplayHud != null) gameplayHud.ResetForNewRun();

            Debug.Log("[Mistake] 重开一局：剧本/进度/评分/车/信任 已复位");
        }

        MistakeContext BuildContext()
        {
            return new MistakeContext
            {
                ElapsedSeconds = Time.timeSinceLevelLoad,
                CompletedSteps = tutorial != null && tutorial.Progress != null ? tutorial.Progress.StepIndex : 0,
                DistanceTravelledM = _distanceM,
                SpeedKmh = sensors != null ? sensors.SpeedKmh : 0f,
                LateralOffsetM = sensors != null ? sensors.LateralOffsetM : 0f,
                LaneGeometryValid = sensors != null && sensors.LaneGeometryValid,
                DistanceAheadToHazardM = sensors != null ? sensors.DistanceAheadToHazardM : float.PositiveInfinity,
            };
        }

        void TrackDistance()
        {
            Vector3 delta = transform.position - _lastPosition;
            delta.y = 0f;
            _distanceM += delta.magnitude;
            _lastPosition = transform.position;
        }

        void OnOpened(MistakeDefinition def)
        {
            if (def.Id == MistakeCatalog.ForgotToBrake.Id) PlaceHazardAhead();

            Debug.Log(string.Format("[Mistake] open id={0} 车速={1:0} 偏移={2:0.00} 窗口={3:0}ms",
                def.Id, sensors != null ? sensors.SpeedKmh : 0f,
                sensors != null ? sensors.LateralOffsetM : 0f, def.WindowMs));

            if (announceOnBubble)
            {
                _lastDef = def;
                Speak(def.Panic);
            }

            if (MistakeOpened != null) MistakeOpened(def);
        }

        /// <summary>把「前方那辆车」摆到车头正前方 —— 无论车在哪条路上都必然正前方同车道。</summary>
        void PlaceHazardAhead()
        {
            if (vehicle == null) return;
            if (_hazardCar == null && sensors != null) _hazardCar = sensors.hazardCar;
            if (_hazardCar == null) return;

            Vector3 p = vehicle.transform.position + vehicle.transform.forward * hazardSpawnDistanceM;
            p.y = 0f;
            _hazardCar.position = p;
            SetHazardActive(true);
        }

        void SetHazardActive(bool active)
        {
            if (_hazardCar != null) _hazardCar.gameObject.SetActive(active);
        }

        // ---- 玩家干预的认定 ----

        void OnTurnEvent(TurnEvent evt)
        {
            if (evt == null) return;

            // 只有「在失误窗口打开之后**开始**的这一轮对话」才算得上及时干预：
            // 否则玩家在失误之前那一轮的动作会被误记成救回。
            if (evt.Kind == TurnEventKind.AsrFinal)
            {
                _turnStartedAfterWarning = _director.Phase == MistakePhase.Warning;
                return;
            }

            if (evt.Kind != TurnEventKind.DriveAction || evt.Action == null) return;

            if (_turnStartedAfterWarning)
            {
                _turnStartedAfterWarning = false;
                SubmitPlayerIntent(evt.Action.Intent);
            }
        }

        // ---- 物理结果 ----

        void HandleResolved(MistakeOutcome outcome)
        {
            Session.ObserveMistake(outcome);
            if (!outcome.Saved && !string.IsNullOrEmpty(outcome.LocalEventName))
            {
                ReportLocalEvent(outcome.LocalEventName);
            }

            SetHazardActive(false);

            // 救回来/没救住，她自己有话说 —— 优先播本地语音（声音与字幕同源）
            if (_lastDef != null)
            {
                Speak(outcome.Saved ? _lastDef.Saved : _lastDef.Failed);
                _lastDef = null;
            }

            Debug.Log(string.Format("[Mistake] resolve id={0} kind={1} event={2} reaction={3:0}ms",
                outcome.Id, outcome.Kind, outcome.LocalEventName, outcome.ReactionMs));

            if (MistakeResolved != null) MistakeResolved(outcome);
        }

        /// <summary>
        /// 说一句：先试本地语音库，**播到了就用实际播出的那句做字幕**（保证听到=看到）；
        /// 没播到（语音库拿不到 / 该条没有 wav）才退回 Core 里的兜底文本。
        /// </summary>
        void Speak(VoiceCue cue)
        {
            if (cue == null) return;

            string played = cue.HasVoice && localVoice != null
                ? localVoice.PlayById(cue.Id, ttsPlayer)
                : null;

            string text = played ?? cue.Text;
            if (bubble != null && !string.IsNullOrEmpty(text)) bubble.ShowLine(text);
        }

        void OnCollisionDetected(float impactKmh, string otherName)
        {
            // 窗口开着 → 交给失误结案（顺带用撞车事件名）；否则按「独立撞车」上报
            if (_director.ReportCollision()) return;
            ReportLocalEvent("collision");
        }

        void OnLaneDeparted()
        {
            // 窗口开着 → 吞掉：同一起事故会由失误的物理判据结案，别记两次
            if (_director.Phase == MistakePhase.Warning) return;
            ReportLocalEvent("lane_departure");
        }

        /// <summary>本类是全场景**唯一**的 <c>ReportLocalEvent</c> 出口。</summary>
        void ReportLocalEvent(string eventName)
        {
            if (string.IsNullOrEmpty(eventName)) return;
            if (IsDuplicate(eventName)) return;

            _lastReportedEvent = eventName;
            _lastReportedAt = Time.time;

            Session.ObserveLocalEvent(eventName);
            if (gameState != null)
            {
                gameState.ReportLocalEvent(eventName);
                Session.SetTrust(gameState.Trust);
            }
        }

        bool IsDuplicate(string eventName)
            => string.Equals(eventName, _lastReportedEvent, StringComparison.OrdinalIgnoreCase)
               && Time.time - _lastReportedAt < duplicateWindowSeconds;

        /// <summary>立即结算（HUD 的调试键 / 关卡结束时用）。</summary>
        public SettlementReport EndLevel()
        {
            if (gameState != null) Session.SetTrust(gameState.Trust);
            return Session.Settle();
        }
    }
}
