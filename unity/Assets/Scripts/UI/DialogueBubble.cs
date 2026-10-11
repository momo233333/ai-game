using System.Text;
using Copilot.AI;
using Copilot.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Copilot.UI
{
    /// <summary>
    /// 对话气泡（方案 §1.2 UIManager）：<c>llm_delta</c> 打字机上屏，玩家的话以「你：」回显。
    /// 不依赖 TextMeshPro，直接用 UGUI Text，减少工程依赖。
    /// </summary>
    public sealed class DialogueBubble : MonoBehaviour
    {
        public CopilotConnection connection;
        public Text label;

        [Tooltip("把识别到的玩家原话短暂上屏")]
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

        CopilotConnection _bound;

        void Reset()
        {
            label = GetComponent<Text>();
        }

        void OnEnable()
        {
            Rebind();
        }

        /// <summary>幂等绑定（AddComponent 会在字段注入前触发 OnEnable，装配方注入后再调一次）。</summary>
        public void Rebind()
        {
            if (_bound == connection) return;
            if (_bound != null) _bound.TurnEventReceived -= HandleTurnEvent;
            _bound = connection;
            if (_bound != null) _bound.TurnEventReceived += HandleTurnEvent;
        }

        void OnDisable()
        {
            if (_bound != null)
            {
                _bound.TurnEventReceived -= HandleTurnEvent;
                _bound = null;
            }
        }

        void Start()
        {
            Rebind();
        }

        void HandleTurnEvent(TurnEvent evt)
        {
            if (evt == null) return;

            switch (evt.Kind)
            {
                case TurnEventKind.LlmDelta:
                    BeginLine();
                    _buffer.Append(evt.Text);
                    break;

                case TurnEventKind.AsrFinal:
                    if (showAsrEcho && !string.IsNullOrEmpty(evt.Text))
                    {
                        SetImmediate("你：" + evt.Text);
                    }
                    break;

                case TurnEventKind.TtsAudio:
                    // 语音库里兜底的台词也要上屏（此时通常没有 llm_delta 文本）
                    if (_buffer.Length == 0 && !string.IsNullOrEmpty(evt.Text))
                    {
                        BeginLine();
                        _buffer.Append(evt.Text);
                    }
                    _visibleChars = Mathf.Max(_visibleChars, _buffer.Length);
                    break;
            }
        }

        /// <summary>直接上屏一句话（不走打字机）——给关卡播报之类的外部调用用。</summary>
        public void ShowLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            SetImmediate(text);
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