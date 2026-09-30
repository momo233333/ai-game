using System.Text;
using Copilot.AI;
using Copilot.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Copilot.UI
{
    /// <summary>
    /// 对话气泡（方案 §1.2 UIManager）：llm_delta 打字机上屏，tts 结束收尾。
    /// 不依赖 TextMeshPro，直接用 UGUI Text，减少工程依赖。
    /// </summary>
    public sealed class DialogueBubble : MonoBehaviour
    {
        public AIClient client;
        public Text label;
        [Tooltip("玩家听到/看到的最新台词（含服务端预置话术）")]
        public bool showAsrEcho = true;

        [Header("打字机")]
        public float charsPerSecond = 20f;
        [Range(0f, 1f)] public float alphaWhenIdle = 0.15f;

        readonly StringBuilder _buffer = new StringBuilder();
        string _rendered = "";
        float _visibleChars;
        bool _active;

        public string CurrentText
        {
            get { return _rendered; }
        }

        void Reset()
        {
            label = GetComponent<Text>();
        }

        void OnEnable()
        {
            if (client != null) client.TurnEventReceived += HandleTurnEvent;
        }

        void OnDisable()
        {
            if (client != null) client.TurnEventReceived -= HandleTurnEvent;
        }

        void HandleTurnEvent(TurnEvent evt)
        {
            if (evt == null) return;
            switch (evt.Kind)
            {
                case "llm_delta":
                    BeginLine();
                    _buffer.Append(evt.Text);
                    break;
                case "asr_final":
                    if (showAsrEcho && !string.IsNullOrEmpty(evt.Text))
                    {
                        // 玩家的话以「你：」前缀短暂上屏
                        SetImmediate("你：" + evt.Text);
                    }
                    break;
                case "tts_audio":
                    string text = Copilot.Core.SimpleJson.GetString(evt.Data, "text");
                    if (_buffer.Length == 0 && !string.IsNullOrEmpty(text))
                    {
                        BeginLine();
                        _buffer.Append(text);
                    }
                    _visibleChars = Mathf.Max(_visibleChars, _buffer.Length);
                    break;
            }
        }

        void BeginLine()
        {
            if (_active) return;
            _active = true;
            _buffer.Length = 0;
            _visibleChars = 0f;
        }

        void SetImmediate(string text)
        {
            _buffer.Length = 0;
            _buffer.Append(text);
            _visibleChars = _buffer.Length;
            _active = false;
            _rendered = text;
            if (label != null) label.text = text;
        }

        void Update()
        {
            if (_buffer.Length == 0)
            {
                if (label != null && !string.IsNullOrEmpty(label.text))
                {
                    var color = label.color;
                    color.a = Mathf.MoveTowards(color.a, alphaWhenIdle, Time.deltaTime * 2f);
                    label.color = color;
                }
                return;
            }

            _visibleChars = Mathf.Min(_buffer.Length, _visibleChars + charsPerSecond * Time.deltaTime);
            int shown = Mathf.FloorToInt(_visibleChars);
            string next = _buffer.ToString(0, shown);
            if (next != _rendered)
            {
                _rendered = next;
                if (label != null)
                {
                    label.text = _rendered;
                    var color = label.color;
                    color.a = 1f;
                    label.color = color;
                }
            }

            if (shown >= _buffer.Length) _active = false;
        }
    }
}
