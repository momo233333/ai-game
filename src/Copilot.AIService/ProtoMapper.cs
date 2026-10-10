using Google.Protobuf;
using Core = Copilot.Core;
using Proto = Copilot.Contracts;

namespace Copilot.AIService;

/// <summary>
/// 领域模型 ↔ protobuf 映射（IT方案1.md V2.0 §4.2）。
/// Contracts 只放协议、Core 只放纯逻辑，映射集中在伴生进程这一层。
/// </summary>
public static class ProtoMapper
{
    public static Proto.Emotion ToProto(Core.Emotion emotion) => emotion switch
    {
        Core.Emotion.Annoyed => Proto.Emotion.Annoyed,
        Core.Emotion.Nervous => Proto.Emotion.Nervous,
        Core.Emotion.Happy => Proto.Emotion.Happy,
        _ => Proto.Emotion.Calm,
    };

    public static Core.Emotion ToDomain(Proto.Emotion emotion) => emotion switch
    {
        Proto.Emotion.Annoyed => Core.Emotion.Annoyed,
        Proto.Emotion.Nervous => Core.Emotion.Nervous,
        Proto.Emotion.Happy => Core.Emotion.Happy,
        _ => Core.Emotion.Calm,
    };

    public static Proto.DriveAction ToProto(Core.DriveAction a) => new()
    {
        Steering = a.Steering,
        Throttle = a.Throttle,
        Brake = a.Brake,
        HoldMs = a.HoldMs,
        Intent = a.Intent,
        Confidence = a.Confidence,
    };

    public static Proto.TurnEvent ToProto(Core.TurnEvent e)
    {
        var message = new Proto.TurnEvent();
        switch (e.Kind)
        {
            case Core.TurnEventKind.AsrPartial:
                message.AsrPartial = new Proto.AsrPartial { Text = e.Text ?? string.Empty, Confidence = e.Confidence };
                break;

            case Core.TurnEventKind.AsrFinal:
                message.AsrFinal = new Proto.AsrFinal
                {
                    Text = e.Text ?? string.Empty,
                    Confidence = e.Confidence,
                    LowConfidence = e.LowConfidence,
                };
                break;

            case Core.TurnEventKind.LlmDelta:
                message.LlmDelta = new Proto.LlmDelta { Text = e.Text ?? string.Empty };
                break;

            case Core.TurnEventKind.DriveAction:
                message.DriveAction = ToProto(e.Action ?? new Core.DriveAction());
                break;

            case Core.TurnEventKind.TtsAudio:
                message.TtsAudio = new Proto.TtsAudio
                {
                    Pcm16 = ByteString.CopyFrom(e.Pcm16 ?? Array.Empty<byte>()),
                    FromVoicelib = e.FromVoiceLib,
                    Text = e.Text ?? string.Empty,
                    SampleRate = e.SampleRate,
                };
                break;

            case Core.TurnEventKind.StateDelta:
                var s = e.State ?? new Core.StateDelta();
                message.StateDelta = new Proto.StateDelta
                {
                    Trust = s.Trust,
                    Emotion = ToProto(s.Emotion),
                    TrustDelta = s.TrustDelta,
                };
                break;

            case Core.TurnEventKind.Error:
                message.Error = new Proto.ErrorEvent
                {
                    Code = e.ErrorCode ?? "error",
                    Message = e.ErrorMessage ?? string.Empty,
                };
                break;
        }

        return message;
    }

    public static Core.GameStateSnapshot ToDomain(Proto.GameState g) => new()
    {
        Trust = g.Trust,
        Emotion = ToDomain(g.Emotion),
        LevelId = g.LevelId ?? string.Empty,
        Scene = new Core.SceneContext
        {
            SceneId = g.Scene?.SceneId ?? string.Empty,
            Weather = g.Scene?.Weather ?? string.Empty,
            TimeOfDay = g.Scene?.TimeOfDay ?? string.Empty,
        },
        Telemetry = new Core.VehicleTelemetry
        {
            SpeedKmh = g.Telemetry?.SpeedKmh ?? 0f,
            Gear = g.Telemetry?.Gear ?? 0,
            AtIntersection = g.Telemetry?.AtIntersection ?? false,
            ObstacleSummary = g.Telemetry?.ObstacleSummary ?? string.Empty,
        },
    };

    public static Proto.GameState ToProto(Core.GameStateSnapshot s) => new()
    {
        Trust = s.Trust,
        Emotion = ToProto(s.Emotion),
        LevelId = s.LevelId ?? string.Empty,
        Scene = new Proto.SceneContext
        {
            SceneId = s.Scene?.SceneId ?? string.Empty,
            Weather = s.Scene?.Weather ?? string.Empty,
            TimeOfDay = s.Scene?.TimeOfDay ?? string.Empty,
        },
        Telemetry = new Proto.VehicleTelemetry
        {
            SpeedKmh = s.Telemetry?.SpeedKmh ?? 0f,
            Gear = s.Telemetry?.Gear ?? 0,
            AtIntersection = s.Telemetry?.AtIntersection ?? false,
            ObstacleSummary = s.Telemetry?.ObstacleSummary ?? string.Empty,
        },
    };
}