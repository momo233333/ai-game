using Copilot.Core;
using Copilot.State;
using UnityEngine;
using UnityEngine.UI;

namespace Copilot.UI
{
    /// <summary>
    /// 表情切换（方案 §5.2：情绪档位变化触发表现层切换）。
    /// 没配 Sprite 时退化为按情绪改色，方便灰盒阶段直接跑。
    /// </summary>
    public sealed class EmotionFace : MonoBehaviour
    {
        public GameStateManager gameState;
        public Image faceImage;
        public Sprite calm;
        public Sprite annoyed;
        public Sprite nervous;
        public Sprite happy;

        [Header("缺图时的兜底颜色")]
        public Color calmColor = new Color(0.85f, 0.9f, 1f);
        public Color annoyedColor = new Color(1f, 0.75f, 0.6f);
        public Color nervousColor = new Color(0.75f, 1f, 0.8f);
        public Color happyColor = new Color(1f, 0.95f, 0.6f);

        void Reset()
        {
            faceImage = GetComponent<Image>();
        }

        void OnEnable()
        {
            if (gameState != null) gameState.StateChanged += OnStateChanged;
        }

        void OnDisable()
        {
            if (gameState != null) gameState.StateChanged -= OnStateChanged;
        }

        void Start()
        {
            if (gameState != null) Apply(gameState.Emotion);
        }

        void OnStateChanged(float trust, Emotion emotion, float delta)
        {
            Apply(emotion);
        }

        public void Apply(Emotion emotion)
        {
            Sprite sprite = null;
            Color color;
            switch (emotion)
            {
                case Emotion.Annoyed:
                    sprite = annoyed;
                    color = annoyedColor;
                    break;
                case Emotion.Nervous:
                    sprite = nervous;
                    color = nervousColor;
                    break;
                case Emotion.Happy:
                    sprite = happy;
                    color = happyColor;
                    break;
                default:
                    sprite = calm;
                    color = calmColor;
                    break;
            }

            if (faceImage == null) return;
            if (sprite != null)
            {
                faceImage.sprite = sprite;
                faceImage.color = Color.white;
            }
            else
            {
                faceImage.color = color;
            }
        }
    }
}
