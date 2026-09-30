using System;
using System.Collections.Generic;

namespace Copilot.Core
{
    public enum Emotion
    {
        Calm = 0,
        Annoyed = 1,
        Nervous = 2,
        Happy = 3,
    }

    /// <summary>与 ai-service/core/types.py 的 Intent 白名单保持一致。</summary>
    public static class Intents
    {
        public const string TurnLeft = "TURN_LEFT";
        public const string TurnRight = "TURN_RIGHT";
        public const string Accelerate = "ACCELERATE";
        public const string Brake = "BRAKE";
        public const string LaneChange = "LANE_CHANGE";
        public const string Park = "PARK";
        public const string Ignore = "IGNORE";
        public const string QuestionBack = "QUESTION_BACK";

        static readonly HashSet<string> Allowed = new HashSet<string>
        {
            TurnLeft, TurnRight, Accelerate, Brake, LaneChange, Park, Ignore, QuestionBack,
        };

        public static bool IsAllowed(string intent)
        {
            return intent != null && Allowed.Contains(intent);
        }
    }

    /// <summary>结构化驾驶指令（协议 §3.1 DriveAction）。</summary>
    public sealed class DriveAction
    {
        public float Steering;
        public float Throttle;
        public float Brake;
        public int HoldMs = 600;
        public string Intent = Intents.Ignore;
        public float Confidence;
        public bool Refused;
        public string RefuseReason = "";

        public static DriveAction FromData(Dictionary<string, object> data)
        {
            var action = new DriveAction
            {
                Steering = SimpleJson.GetFloat(data, "steering"),
                Throttle = SimpleJson.GetFloat(data, "throttle"),
                Brake = SimpleJson.GetFloat(data, "brake"),
                HoldMs = SimpleJson.GetInt(data, "hold_ms", 600),
                Intent = SimpleJson.GetString(data, "intent", Intents.Ignore),
                Confidence = SimpleJson.GetFloat(data, "confidence"),
                Refused = SimpleJson.GetBool(data, "refused"),
                RefuseReason = SimpleJson.GetString(data, "refuse_reason"),
            };
            return action;
        }
    }

    /// <summary>下行事件：kind + data，与 JSON-TCP / gRPC 适配层同构。</summary>
    public sealed class TurnEvent
    {
        public string Kind;
        public Dictionary<string, object> Data;

        public TurnEvent(string kind, Dictionary<string, object> data)
        {
            Kind = kind;
            Data = data ?? new Dictionary<string, object>();
        }

        public static TurnEvent Parse(string jsonLine)
        {
            object root = SimpleJson.Parse(jsonLine);
            var dict = SimpleJson.AsDict(root);
            if (dict == null) throw new FormatException("event must be a JSON object");
            string kind = SimpleJson.GetString(dict, "kind");
            if (string.IsNullOrEmpty(kind)) throw new FormatException("event missing 'kind'");
            var data = SimpleJson.AsDict(dict.ContainsKey("data") ? dict["data"] : null)
                       ?? new Dictionary<string, object>();
            return new TurnEvent(kind, data);
        }

        public static bool TryParse(string jsonLine, out TurnEvent evt)
        {
            try
            {
                evt = Parse(jsonLine);
                return true;
            }
            catch (Exception)
            {
                evt = null;
                return false;
            }
        }

        public DriveAction ToDriveAction()
        {
            return DriveAction.FromData(Data);
        }

        public string Text
        {
            get { return SimpleJson.GetString(Data, "text"); }
        }

        public string ReplyText
        {
            get { return SimpleJson.GetString(Data, "reply", Text); }
        }
    }

    public static class EmotionUtil
    {
        public static Emotion FromName(string name)
        {
            switch ((name ?? "").ToUpperInvariant())
            {
                case "ANNOYED": return Emotion.Annoyed;
                case "NERVOUS": return Emotion.Nervous;
                case "HAPPY": return Emotion.Happy;
                default: return Emotion.Calm;
            }
        }

        public static string ToName(Emotion emotion)
        {
            switch (emotion)
            {
                case Emotion.Annoyed: return "ANNOYED";
                case Emotion.Nervous: return "NERVOUS";
                case Emotion.Happy: return "HAPPY";
                default: return "CALM";
            }
        }
    }
}
