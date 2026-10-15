using System.Text;
using Copilot.Core.Gameplay;
using Copilot.Gameplay;
using UnityEngine;
using UnityEngine.UI;

namespace Copilot.UI
{
    /// <summary>
    /// 玩法层表现：**警告横幅 + 倒计时 / 实时分数 / 结算面板**。
    ///
    /// 只读 <see cref="MistakeRunner"/> 与 <c>TutorialDirector</c> 的公开状态，不自己算规则
    /// —— 判定全在 <c>Copilot.Core.Gameplay</c>，这里只负责"让人看得见"。
    /// </summary>
    public sealed class GameplayHud : MonoBehaviour
    {
        public MistakeRunner runner;
        public TutorialDirector tutorial;

        [Header("警告横幅（她在犯错，等你开口）")]
        public Image bannerBg;
        public Text bannerLabel;

        [Header("实时分数")]
        public Text scoreLabel;

        [Header("结算面板")]
        public GameObject settlementPanel;
        public Text settlementTitle;
        public Text settlementStats;
        public Text settlementStars;
        public Text settlementItems;

        [Header("节奏")]
        [Tooltip("教学关走完后隔多久弹结算面板（先让小雅把话说完）")]
        public float settleDelaySeconds = 1.5f;

        [Tooltip("结案后横幅闪绿/闪红多久")]
        public float flashSeconds = 1.0f;

        [Header("调试")]
        [Tooltip("手动结算（免走完六步就能看面板）")]
        public KeyCode settleKey = KeyCode.Return;

        [SerializeField] Color _savedColor = new Color(0.3f, 0.9f, 0.45f, 0.85f);
        [SerializeField] Color _failedColor = new Color(0.9f, 0.3f, 0.25f, 0.85f);
        [SerializeField] Color _warningColor = new Color(0.95f, 0.6f, 0.15f, 0.85f);

        bool _settled;
        float _completedAt = -1f;
        float _flashUntil = -1f;
        string _flashText = string.Empty;
        Color _flashColor = Color.white;

        MistakeRunner _boundRunner;

        void OnEnable()
        {
            Rebind();
        }

        void Start()
        {
            Rebind();
            SetBannerVisible(false);
        }

        void OnDisable()
        {
            Rebind(null);
        }

        /// <summary>幂等绑定（AddComponent 先于字段注入触发 OnEnable，装配方注入后再调一次）。</summary>
        public void Rebind()
        {
            Rebind(runner);
        }

        void Rebind(MistakeRunner target)
        {
            if (_boundRunner == target) return;
            if (_boundRunner != null)
            {
                _boundRunner.MistakeOpened -= OnMistakeOpened;
                _boundRunner.MistakeResolved -= OnMistakeResolved;
            }
            _boundRunner = target;
            if (_boundRunner != null)
            {
                _boundRunner.MistakeOpened += OnMistakeOpened;
                _boundRunner.MistakeResolved += OnMistakeResolved;
            }
        }

        void Update()
        {
            UpdateBanner();
            UpdateScore();
            UpdateSettlement();
        }

        void UpdateBanner()
        {
            bool warning = runner != null && runner.Phase == MistakePhase.Warning && runner.Current != null;

            if (warning)
            {
                var def = runner.Current;
                float remainSec = Mathf.CeilToInt(runner.RemainingMs / 1000f);
                if (bannerLabel != null)
                {
                    bannerLabel.text = def.Title + "\n快喊她！剩 " + Mathf.Max(remainSec, 0f) + " 秒";
                }

                // 背景脉冲：这是"来不及了"的视觉压力
                if (bannerBg != null)
                {
                    float pulse = 0.55f + 0.35f * Mathf.PingPong(Time.time * 2.2f, 1f);
                    bannerBg.color = new Color(_warningColor.r, _warningColor.g, _warningColor.b, pulse);
                }

                SetBannerVisible(true);
                return;
            }

            if (Time.time < _flashUntil)
            {
                if (bannerLabel != null) bannerLabel.text = _flashText;
                if (bannerBg != null) bannerBg.color = _flashColor;
                SetBannerVisible(true);
                return;
            }

            SetBannerVisible(false);
        }

        void UpdateScore()
        {
            if (scoreLabel == null) return;
            if (runner == null || runner.Session == null)
            {
                scoreLabel.text = string.Empty;
                return;
            }
            scoreLabel.text = "本局评分 " + runner.Session.LiveScore();
        }

        void UpdateSettlement()
        {
            if (runner == null) return;

            if (!_settled && tutorial != null && tutorial.Progress != null && tutorial.Progress.IsCompleted)
            {
                if (_completedAt < 0f) _completedAt = Time.time;
                if (Time.time - _completedAt >= settleDelaySeconds) ShowSettlement();
            }

            if (!_settled && Input.GetKeyDown(settleKey)) ShowSettlement();
        }

        void OnMistakeOpened(MistakeDefinition def)
        {
            _flashUntil = -1f;   // 横幅立刻切到警告态
        }

        void OnMistakeResolved(MistakeOutcome outcome)
        {
            _flashText = outcome.Saved ? "好，你喊住了她" : "没喊住 —— " + DescribeEvent(outcome.LocalEventName);
            _flashColor = outcome.Saved ? _savedColor : _failedColor;
            _flashUntil = Time.time + flashSeconds;
        }

        static string DescribeEvent(string eventName)
        {
            switch (eventName)
            {
                case "collision": return "撞上了";
                case "lane_departure": return "压线了";
                case "hard_brake": return "她差点追尾";
                default: return string.IsNullOrEmpty(eventName) ? "出事了" : eventName;
            }
        }

        void SetBannerVisible(bool visible)
        {
            if (bannerBg != null && bannerBg.enabled != visible) bannerBg.enabled = visible;
            if (bannerLabel != null && bannerLabel.enabled != visible) bannerLabel.enabled = visible;
        }

        /// <summary>弹结算面板（也由 Enter 手动触发）。</summary>
        public void ShowSettlement()
        {
            if (_settled || runner == null || runner.Session == null) return;
            _settled = true;

            var report = runner.EndLevel();
            SetBannerVisible(false);

            if (settlementPanel != null) settlementPanel.SetActive(true);
            if (settlementTitle != null) settlementTitle.text = "教学关结算";
            if (settlementStars != null) settlementStars.text = StarText(report.Stars);
            if (settlementStats != null)
            {
                settlementStats.text =
                    "安全  " + report.SafetyPoints + " / 40\n" +
                    "及时干预  " + report.InterventionPoints + " / 40\n" +
                    "教学完成  " + report.TutorialPoints + " / 20\n" +
                    "总分  " + report.Score + " / 100";
            }
            if (settlementItems != null) settlementItems.text = FormatItems(report, runner.Session.TotalSteps);

            Debug.Log(string.Format("[Gameplay] 结算 总分={0} 星级={1}（安全{2} 干预{3} 教学{4}）",
                report.Score, report.Stars, report.SafetyPoints, report.InterventionPoints, report.TutorialPoints));
        }

        static string StarText(int stars)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 3; i++) sb.Append(i < stars ? '★' : '☆');
            return sb.ToString();
        }

        static string FormatItems(SettlementReport report, int totalSteps)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < report.Items.Count; i++)
            {
                var item = report.Items[i];
                sb.Append(item.Label);
                sb.Append("：");

                if (item.Label == "教学步")
                {
                    sb.Append(item.Value.ToString("0"));
                    sb.Append('/');
                    sb.Append(totalSteps);
                }
                else
                {
                    sb.Append(item.Value.ToString("0.##"));
                }

                if (item.Points != 0)
                {
                    sb.Append("（");
                    if (item.Points > 0) sb.Append('+');
                    sb.Append(item.Points);
                    sb.Append("）");
                }

                if (i < report.Items.Count - 1) sb.Append('\n');
            }
            return sb.ToString();
        }
    }
}
