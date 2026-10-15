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

        TutorialProgress _progress;

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
            Refresh();
        }

        void OnDestroy()
        {
            if (connection != null) connection.TurnEventReceived -= OnTurnEvent;
        }

        void OnTurnEvent(TurnEvent e)
        {
            if (e == null || e.Kind != TurnEventKind.DriveAction || e.Action == null) return;
            if (!_progress.Observe(e.Action.Intent)) return;

            Refresh();

            if (_progress.IsCompleted && announceCompletion && bubble != null)
            {
                bubble.ShowLine("教练……我、我全都做下来了！");
            }
        }

        /// <summary>把当前步骤刷到 HUD。</summary>
        public void Refresh()
        {
            if (stepLabel == null) return;
            var level = _progress.Level;

            if (_progress.IsCompleted)
            {
                stepLabel.text = $"【{level.Subject}】{level.Title}　✅ 通过";
                if (resultLabel != null) resultLabel.text = "教学关完成 —— 科目二 · 场地驾驶 待开放";
                return;
            }

            var step = _progress.Current!;
            stepLabel.text =
                $"【{level.Subject}】{level.Title}　{_progress.StepIndex + 1}/{_progress.TotalSteps}\n" +
                $"本节：{step.Title} —— {step.Instruction}\n" +
                $"照着念：「{step.ExamplePhrase}」";
            if (resultLabel != null) resultLabel.text = string.Empty;
        }
    }
}