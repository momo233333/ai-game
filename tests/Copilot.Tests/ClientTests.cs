using Copilot.AIService;
using Copilot.Client;
using Contracts = Copilot.Contracts;
using Core = Copilot.Core;
using Xunit;

namespace Copilot.Tests;

/// <summary>
/// 起真 gRPC 服务端，用 <see cref="CopilotClient"/>（Unity 侧将用的同一个客户端）走一遍四个 RPC。
/// 与 <see cref="GrpcServerFixture"/> 的区别：那边验服务端契约，这边验**前端接入层**——
/// 包括 proto→<see cref="Core.TurnEvent"/> 的映射是否保真。
/// </summary>
public sealed class ClientFixture : IAsyncLifetime
{
    private const int Port = 50998;

    private AiRuntime? _runtime;
    private CancellationTokenSource? _cts;
    private Task? _serverTask;

    public string Url => $"http://127.0.0.1:{Port}";

    public async Task InitializeAsync()
    {
        var config = Config.Load();
        config.Server.Url = Url;
        // 与模型无关：钉死规则后端 + 关掉 ASR（同 GrpcServerFixture，理由见技术债 #8）
        config.Llm.Backend = "rule";
        config.Asr.Backend = "none";
        _runtime = AiRuntime.Create(config);

        _cts = new CancellationTokenSource();
        _serverTask = ServerHost.RunAsync(_runtime, _cts.Token);

        using var probe = new CopilotClient(Url);
        Exception? last = null;
        for (int i = 0; i < 100; i++)
        {
            try
            {
                await probe.HealthAsync();
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(100);
            }
        }

        throw new InvalidOperationException("gRPC 服务端在 10s 内未就绪。最后一次错误：" + last);
    }

    public async Task DisposeAsync()
    {
        if (_cts != null) await _cts.CancelAsync();
        if (_serverTask != null)
        {
            try { await _serverTask; } catch (OperationCanceledException) { }
        }
        _cts?.Dispose();
        _runtime?.Dispose();
    }
}

public class CopilotClientTests : IClassFixture<ClientFixture>
{
    private readonly ClientFixture _fixture;

    public CopilotClientTests(ClientFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task 健康检查_拿到各后端降级状态()
    {
        using var client = new CopilotClient(_fixture.Url);

        var health = await client.HealthAsync();

        Assert.True(health.Ok);
        Assert.Equal("rule", health.LlmBackend);
        Assert.Equal("voicelib", health.TtsBackend);
        Assert.False(string.IsNullOrEmpty(health.Version));
    }

    [Fact]
    public async Task 文本回合_事件顺序为指令先于台词_并带回真音频()
    {
        using var client = new CopilotClient(_fixture.Url);

        var events = new List<Core.TurnEvent>();
        await foreach (var e in client.TextTurnAsync("前面路口左转")) events.Add(e);

        Assert.Equal(Core.TurnEventKind.AsrFinal, events[0].Kind);

        int drive = events.FindIndex(e => e.Kind == Core.TurnEventKind.DriveAction);
        int reply = events.FindIndex(e => e.Kind == Core.TurnEventKind.LlmDelta);
        int tts = events.FindIndex(e => e.Kind == Core.TurnEventKind.TtsAudio);

        Assert.True(drive >= 0 && drive < reply, "drive_action 必须先于台词");
        Assert.True(tts > reply, "tts_audio 应在台词之后");

        Assert.Equal(Core.Intents.TurnLeft, events[drive].Action!.Intent);
        Assert.True(events[tts].FromVoiceLib);
        Assert.Equal(24000, events[tts].SampleRate);
        Assert.NotEmpty(events[tts].Pcm16!);
    }

    [Fact]
    public async Task 状态同步后_低速停车指令被放行()
    {
        using var client = new CopilotClient(_fixture.Url);

        var ok = await client.SyncGameStateAsync(new Core.GameStateSnapshot
        {
            Trust = 33f,
            Emotion = Core.Emotion.Nervous,
            LevelId = "level_test",
            Telemetry = new Core.VehicleTelemetry { SpeedKmh = 5f },
        });
        Assert.True(ok);

        var events = new List<Core.TurnEvent>();
        await foreach (var e in client.TextTurnAsync("靠边停车")) events.Add(e);

        var action = events.First(e => e.Kind == Core.TurnEventKind.DriveAction).Action!;
        Assert.Equal(Core.Intents.Park, action.Intent);
    }

    [Fact]
    public async Task 语音回合_ASR缺失时返回明确错误事件()
    {
        using var client = new CopilotClient(_fixture.Url);

        var events = new List<Core.TurnEvent>();
        await foreach (var e in client.VoiceTurnAsync(new byte[3200])) events.Add(e);

        var error = events.First(e => e.Kind == Core.TurnEventKind.Error);
        Assert.Equal("asr_unavailable", error.ErrorCode);
    }
}

/// <summary>映射保真度（不需要服务端）。</summary>
public class ClientProtoMapperTests
{
    [Fact]
    public void 驱动动作_数值与意图逐字段保真()
    {
        var e = ClientProtoMapper.ToDomain(new Contracts.TurnEvent
        {
            DriveAction = new Contracts.DriveAction
            {
                Steering = -0.6f,
                Throttle = 0.3f,
                Brake = 0f,
                HoldMs = 1200,
                Intent = Core.Intents.TurnLeft,
                Confidence = 0.9f,
            },
        });

        Assert.Equal(Core.TurnEventKind.DriveAction, e.Kind);
        Assert.Equal(-0.6f, e.Action!.Steering, 3);
        Assert.Equal(0.3f, e.Action.Throttle, 3);
        Assert.Equal(1200, e.Action.HoldMs);
        Assert.Equal(Core.Intents.TurnLeft, e.Action.Intent);
    }

    [Fact]
    public void 语音音频_裸PCM与采样率保真()
    {
        var pcm = new byte[] { 1, 2, 3, 4 };

        var e = ClientProtoMapper.ToDomain(new Contracts.TurnEvent
        {
            TtsAudio = new Contracts.TtsAudio
            {
                Pcm16 = Google.Protobuf.ByteString.CopyFrom(pcm),
                FromVoicelib = true,
                SampleRate = 24000,
                Text = "行，左转交给我",
            },
        });

        Assert.Equal(Core.TurnEventKind.TtsAudio, e.Kind);
        Assert.Equal(pcm, e.Pcm16!);
        Assert.Equal(24000, e.SampleRate);
        Assert.True(e.FromVoiceLib);
        Assert.Equal("行，左转交给我", e.Text);
    }

    [Fact]
    public void 状态差量_含情绪枚举映射()
    {
        var e = ClientProtoMapper.ToDomain(new Contracts.TurnEvent
        {
            StateDelta = new Contracts.StateDelta
            {
                Trust = 55f,
                Emotion = Contracts.Emotion.Annoyed,
                TrustDelta = -5f,
            },
        });

        Assert.Equal(Core.TurnEventKind.StateDelta, e.Kind);
        Assert.Equal(55f, e.State!.Trust, 3);
        Assert.Equal(Core.Emotion.Annoyed, e.State.Emotion);
        Assert.Equal(-5f, e.State.TrustDelta, 3);
    }

    [Fact]
    public void 空事件_降级为错误事件而不是抛异常()
    {
        var e = ClientProtoMapper.ToDomain(new Contracts.TurnEvent());

        Assert.Equal(Core.TurnEventKind.Error, e.Kind);
        Assert.Equal("unknown_event", e.ErrorCode);
    }

    [Fact]
    public void 游戏状态回推_遥测与场景逐字段保真()
    {
        var proto = ClientProtoMapper.ToProto(new Core.GameStateSnapshot
        {
            Trust = 72f,
            Emotion = Core.Emotion.Happy,
            LevelId = "level_02",
            Scene = new Core.SceneContext { SceneId = "city_02", Weather = "雨", TimeOfDay = "夜" },
            Telemetry = new Core.VehicleTelemetry
            {
                SpeedKmh = 42f,
                Gear = 3,
                AtIntersection = true,
                ObstacleSummary = "前方红灯",
            },
        });

        Assert.Equal(72f, proto.Trust, 3);
        Assert.Equal(Contracts.Emotion.Happy, proto.Emotion);
        Assert.Equal("level_02", proto.LevelId);
        Assert.Equal("city_02", proto.Scene.SceneId);
        Assert.Equal(42f, proto.Telemetry.SpeedKmh, 3);
        Assert.Equal(3, proto.Telemetry.Gear);
        Assert.True(proto.Telemetry.AtIntersection);
        Assert.Equal("前方红灯", proto.Telemetry.ObstacleSummary);
    }
}