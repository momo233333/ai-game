using Copilot.AI;
using Copilot.Core;
using Copilot.Core.Levels;
using Copilot.UI;
using UnityEngine;
using UnityEngine.UI;

namespace Copilot.Gameplay
{
    /// <summary>
    /// 教学关导演（《驾考宝典》科目一）。
    ///
    /// 关卡规则本身在 <see cref="TutorialProgress"/>（Core，纯逻辑可单测）；
    /// 本组件只做两件事：订阅事件流、把当前该做什么显示到 HUD。
    ///
    /// 判定用**学员实际做出来的动作**（<c>drive_action</c> 的 intent），而不是玩家说了什么
    /// —— 这样「玩家说了指令但学员没听懂/不敢做」的情况自然地不算过关，符合教学关的语义。
    /// </summary>
    public sealed class TutorialDirector : MonoBehaviour
    {
        public CopilotConnection connection;

        [Tooltip("步骤提示（左上角那块）")]
        public Text stepLabel;

        [Tooltip("过关/收尾提示")]
        public Text resultLabel;

        [Tooltip("过关后是否把结果也写进字幕")]
        public bool announceCompletion = true;

        public DialogueBubble bubble;

        [Header("结果行颜色")]
        [Tooltip("过关/正常")]
        [SerializeField] Color _goodColor = new Color(0.6f, 0.95f, 0.7f, 1f);
        [Tooltip("做错提示（暖色，让人一眼看出「她做错了」）")]
        [SerializeField] Color _wrongColor = new Color(1f, 0.72f, 0.35f, 1f);

        TutorialProgress _progress;

        /// <summary>本轮教练的原话（来自 <c>AsrFinal</c>）——「减速」那一步要它区分「刹车」与「慢一点」。</summary>
        string? _lastPlayerText;

        /// <summary>
        /// 当前进度。**getter 保证非空**（在 <see cref="Awake"/> 里建），所以调用方不必再判 null
        /// —— 之前它是在 <c>Start</c> 里建的，Start 之前拿到 null 是个坑（编辑模式也不会跑 Start）。
        /// </summary>
        public TutorialProgress Progress
        {
            get
            {
                EnsureProgress();
                return _progress;
            }
        }

        void Awake()
        {
            EnsureProgress();
        }

        void Start()
        {
            EnsureProgress();
            if (connection != null) connection.TurnEventReceived += OnTurnEvent;
            Refresh();
        }

        void EnsureProgress()
        {
            if (_progress == null) _progress = new TutorialProgress(LevelCatalog.Subject1Tutorial);
        }

        /// <summary>重开一局：教学进度回到第一步并刷新 HUD。</summary>
        public void ResetProgress()
        {
            EnsureProgress();
            _progress.Reset();
            _lastPlayerText = null;
            Refresh();
        }

        void OnDestroy()
        {
            if (connection != null) connection.TurnEventReceived -= OnTurnEvent;
        }

        void OnTurnEvent(TurnEvent e)
        {
            if (e == null) return;

            // 先记住教练这一轮的原话：「减速」那一步要靠它区分「减速/刹车」与「慢一点」（见 AcceptSlowRequest）
            if (e.Kind == TurnEventKind.AsrFinal)
            {
                _lastPlayerText = e.Text;
                return;
            }

            if (e.Kind != TurnEventKind.DriveAction || e.Action == null) return;

            // 转向符号 = 方向（TurnPipeline 已跑过 ApplyDirection，Steering 的符号就是左右）
            int direction = e.Action.Steering > 0f ? 1 : e.Action.Steering < 0f ? -1 : 0;

            var observation = _progress.Observe(e.Action.Intent, direction, Intents.IsSlowRequest(_lastPlayerText));
            if (observation == TutorialObservation.NotApplicable) return;

            Refresh(observation);

            if (_progress.IsCompleted && announceCompletion && bubble != null)
            {
                bubble.ShowLine("教练……我、我全都做下来了！");
            }
        }

        /// <summary>刚做对一步时用（会清掉做错提示）。</summary>
        public void Refresh() => Refresh(TutorialObservation.Advanced);

        /// <summary>
        /// 把当前步骤刷到 HUD。
        ///
        /// 四行（issue #2 要求每一步都看得见「目标 / 当前状态 / 成功条件 / 下一步」）：
        /// 关卡+步数 / 本节要做什么 / **做到什么算过** / 照着念的示例口令。
        /// <paramref name="observation"/> 不是 <c>Advanced</c> 时，把「为什么错」写进结果行（暖色）；
        /// 做对了就恢复成绿色。
        /// </summary>
        void Refresh(TutorialObservation observation)
        {
            if (stepLabel == null) return;
            var level = _progress.Level;

            if (_progress.IsCompleted)
            {
                stepLabel.text =
                    $"【{level.Subject}】{level.Title}　{_progress.TotalSteps}/{_progress.TotalSteps}\n" +
                    "✅ 这一课的动作全做下来了。";
                SetResult("教学关完成 —— 科目二 · 场地驾驶 待开放", _goodColor);
                return;
            }

            var step = _progress.Current!;
            stepLabel.text =
                $"【{level.Subject}】{level.Title}　{_progress.StepIndex + 1}/{_progress.TotalSteps}\n" +
                $"本节：{step.Title} —— {step.Instruction}\n" +
                $"目标：{step.SuccessCriteria}\n" +
                $"照着念：「{step.ExamplePhrase}」";

            if (observation == TutorialObservation.Advanced)
            {
                SetResult(string.Empty, _goodColor);
                return;
            }

            string prefix = observation == TutorialObservation.WrongDirection
                ? "方向不对："
                : "动作不对：";
            SetResult(prefix + _progress.LastWrongHint, _wrongColor);
        }

        void SetResult(string text, Color color)
        {
            if (resultLabel == null) return;
            resultLabel.color = color;
            resultLabel.text = text ?? string.Empty;
        }
    }
}