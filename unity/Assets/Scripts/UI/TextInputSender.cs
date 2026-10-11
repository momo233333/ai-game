using Copilot.AI;
using UnityEngine;
using UnityEngine.UI;

namespace Copilot.UI
{
    /// <summary>
    /// 文本输入框 → 文本回合（语音链路还没接好时的调试通道，也用于 CI 式脚本驱动）。
    /// </summary>
    public sealed class TextInputSender : MonoBehaviour
    {
        public CopilotConnection connection;
        public InputField input;
        public Button sendButton;
        public KeyCode submitKey = KeyCode.Return;

        void OnEnable()
        {
            if (sendButton != null) sendButton.onClick.AddListener(Send);
        }

        void OnDisable()
        {
            if (sendButton != null) sendButton.onClick.RemoveListener(Send);
        }

        void Update()
        {
            if (input == null || !input.isFocused) return;
            if (Input.GetKeyDown(submitKey) || Input.GetKeyDown(KeyCode.KeypadEnter)) Send();
        }

        public void Send()
        {
            if (input == null || connection == null) return;
            string text = input.text;
            if (string.IsNullOrEmpty(text)) return;
            connection.SendText(text);
            input.text = "";
        }
    }
}
