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
        Transform _hazardCar;

        Vector3 _lastPosition;
        float _distanceM;
        bool _turnStartedAfterWarning;

        string _lastReportedEvent = string.Empty;
        float _lastReportedAt = -99f;

        CopilotConnection _boundConnection;
        VehicleSensors _boundSensors;

        public MistakePhase Phase => _director.Phase;

        /// <summary>当前正在等干预的失误（Idle 时为 null）。</summary>
        public MistakeDefinition Current => _director.Current;

        public float RemainingMs => _director.RemainingMs;

        public LevelSession Session { get; private set; }

        /// <summary>失误开演（HUD 弹横幅用）。</summary>
        public event Action<MistakeDefinition> MistakeOpened;

        /// <summary>失误结案（HUD 闪绿/闪红用）。</summary>
        public event Action<MistakeOutcome> MistakeResolved;

        void Awake()
        {
            var level = LevelCatalog.Subject1Tutorial;
            Session = new LevelSession(level.Id, level.Steps.Count);
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
            TrackDistance();

            var tick = _director.Tick(Time.deltaTime * 1000f, BuildContext());

            if (tick.Opened && _director.Current != null)
            {
                OnOpened(_director.Current);
            }

            if (tick.InjectNow && tick.InjectAction != null && executor != null)
            {
                // 本地注入：不过 connection，所以不会走到 TutorialDirector（失误不算教学过关）
                executor.Execute(tick.InjectAction);
            }

            if (tutorial != null && tutorial.Progress != null)
            {
                Session.ObserveStepAdvance(tutorial.Progress.StepIndex);
            }
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

            if (announceOnBubble && bubble != null && !string.IsNullOrEmpty(def.PanicLine))
            {
                bubble.ShowLine(def.PanicLine);
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
                _director.ObservePlayerIntent(evt.Action.Intent);
                _turnStartedAfterWarning = false;
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

            Debug.Log(string.Format("[Mistake] resolve id={0} kind={1} event={2} reaction={3:0}ms",
                outcome.Id, outcome.Kind, outcome.LocalEventName, outcome.ReactionMs));

            if (MistakeResolved != null) MistakeResolved(outcome);
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
