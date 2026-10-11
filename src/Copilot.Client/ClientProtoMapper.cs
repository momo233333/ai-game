using System;
using Core = Copilot.Core;
using Proto = Copilot.Contracts;

namespace Copilot.Client
{
    /// <summary>
    /// protobuf ↔ 领域模型映射（客户端方向，与伴生进程的 <c>ProtoMapper</c> 互为逆映射）。
    ///
    /// 前端**只认 <see cref="Core.TurnEvent"/>**：事件形状与 <c>TurnPipeline</c> 产出的完全一致，
    /// 这样 Unity 脚本 / 调试工具 / 测试夹具写一次事件处理，前端换掉也不用改。
    /// </summary>
    public static class ClientProtoMapper
    {
        public static Core.TurnEvent ToDomain(Proto.TurnEvent e)
        {
            if (e == null) return Core.TurnEvent.Error("null_event", "收到空事件");

            switch (e.PayloadCase)
            {
                case Proto.TurnEvent.PayloadOneofCase.AsrPartial:
                    return Core.TurnEvent.AsrPartial(e.AsrPartial.Text, e.AsrPartial.Confidence);

                case Proto.TurnEvent.PayloadOneofCase.AsrFinal:
                    return Core.TurnEvent.AsrFinal(e.AsrFinal.Text, e.AsrFinal.Confidence, e.AsrFinal.LowConfidence);

                case Proto.TurnEvent.PayloadOneofCase.LlmDelta:
                    return Core.TurnEvent.LlmDelta(e.LlmDelta.Text);

                case Proto.TurnEvent.PayloadOneofCase.DriveAction:
                    return Core.TurnEvent.Drive(ToDomain(e.DriveAction));

                case Proto.TurnEvent.PayloadOneofCase.TtsAudio:
                    return Core.TurnEvent.Tts(
                        e.TtsAudio.Pcm16?.ToByteArray() ?? Array.Empty<byte>(),
                        e.TtsAudio.SampleRate,
                        e.TtsAudio.FromVoicelib,
                        e.TtsAudio.Text);

                case Proto.TurnEvent.PayloadOneofCase.StateDelta:
                    return Core.TurnEvent.StateChanged(new Core.StateDelta
                    {
                        Trust = e.StateDelta.Trust,
                        Emotion = ToDomain(e.StateDelta.Emotion),
                        TrustDelta = e.StateDelta.TrustDelta,
                    });

                case Proto.TurnEvent.PayloadOneofCase.Error:
                    return Core.TurnEvent.Error(e.Error.Code, e.Error.Message);

                default:
                    return Core.TurnEvent.Error("unknown_event", $"未知事件类型：{e.PayloadCase}");
            }
        }

        public static Core.DriveAction ToDomain(Proto.DriveAction a) => new Core.DriveAction
        {
            Steering = a.Steering,
            Throttle = a.Throttle,
            Brake = a.Brake,
            HoldMs = a.HoldMs,
            Intent = a.Intent,
            Confidence = a.Confidence,
        };

        public static Core.Emotion ToDomain(Proto.Emotion emotion)
        {
            switch (emotion)
            {
                case Proto.Emotion.Annoyed: return Core.Emotion.Annoyed;
                case Proto.Emotion.Nervous: return Core.Emotion.Nervous;
                case Proto.Emotion.Happy: return Core.Emotion.Happy;
                default: return Core.Emotion.Calm;
            }
        }

        public static Proto.Emotion ToProto(Core.Emotion emotion)
        {
            switch (emotion)
            {
                case Core.Emotion.Annoyed: return Proto.Emotion.Annoyed;
                case Core.Emotion.Nervous: return Proto.Emotion.Nervous;
                case Core.Emotion.Happy: return Proto.Emotion.Happy;
                default: return Proto.Emotion.Calm;
            }
        }

        public static Proto.GameState ToProto(Core.GameStateSnapshot s)
        {
            if (s == null) s = new Core.GameStateSnapshot();
            var telemetry = s.Telemetry ?? new Core.VehicleTelemetry();
            var scene = s.Scene ?? new Core.SceneContext();
            return new Proto.GameState
            {
                Trust = s.Trust,
                Emotion = ToProto(s.Emotion),
                LevelId = s.LevelId ?? string.Empty,
                Scene = new Proto.SceneContext
                {
                    SceneId = scene.SceneId ?? string.Empty,
                    Weather = scene.Weather ?? string.Empty,
                    TimeOfDay = scene.TimeOfDay ?? string.Empty,
                },
                Telemetry = new Proto.VehicleTelemetry
                {
                    SpeedKmh = telemetry.SpeedKmh,
                    Gear = telemetry.Gear,
                    AtIntersection = telemetry.AtIntersection,
                    ObstacleSummary = telemetry.ObstacleSummary ?? string.Empty,
                },
            };
        }
    }
}