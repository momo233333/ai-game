using Copilot.AIService;
using Copilot.Contracts;
using Grpc.Core;
using Grpc.Net.Client;
using Core = Copilot.Core;
using GrpcService = Copilot.Contracts.AIService;
using Xunit;

namespace Copilot.Tests;

/// <summary>
/// 起真 gRPC 服务端（localhost h2c），用真客户端走一遍四个 RPC。
/// 覆盖「伴生进程 ↔ 客户端」的契约面，与 Unity 接入时用的是同一套 proto。
/// </summary>
public sealed class GrpcServerFixture : IAsyncLifetime
{
    private const int Port = 50999;

    private AiRuntime? _runtime;
    private CancellationTokenSource? _cts;
    private Task? _serverTask;

    public GrpcChannel Channel { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        var config = Config.Load();
        config.Server.Url = $"http://127.0.0.1:{Port}";
        // 测试必须与模型无关：钉死规则后端 + 关掉 ASR，
        // 否则一旦 models/ 里有 4.4GB 的 7B，测试会去加载真模型（又慢又依赖 GPU）。
        config.Llm.Backend = "rule";
        config.Asr.Backend = "none";
        _runtime = AiRuntime.Create(config);

        _cts = new CancellationTokenSource();
        _serverTask = ServerHost.RunAsync(_runtime, _cts.Token);

        Channel = GrpcChannel.ForAddress($"http://127.0.0.1:{Port}");
        var client = new GrpcService.AIServiceClient(Channel);

        for (int i = 0; i < 100; i++)
        {
            try
            {
                await client.PingAsync(new Empty());
                return;
            }
            catch (Exception)
            {
                await Task.Delay(100);
            }
        }

        throw new InvalidOperationException("gRPC 服务端在 10s 内未就绪");
    }

    public async Task DisposeAsync()
    {
        Channel?.Dispose();
        if (_cts != null) await _cts.CancelAsync();
        if (_serverTask != null)
        {
            try { await _serverTask; } catch (OperationCanceledException) { }
        }
        _cts?.Dispose();
        _runtime?.Dispose();
    }
}

public class GrpcServerTests : IClassFixture<GrpcServerFixture>
{
    private readonly GrpcServerFixture _fixture;

    public GrpcServerTests(GrpcServerFixture fixture) => _fixture = fixture;

    private GrpcService.AIServiceClient Client() => new(_fixture.Channel);

    [Fact]
    public async Task Ping返回降级后的后端状态()
    {
        var health = await Client().PingAsync(new Empty());

        Assert.True(health.Ok);
        Assert.Equal("rule", health.LlmBackend);
        Assert.Equal("voicelib", health.TtsBackend);
        Assert.False(string.IsNullOrEmpty(health.Version));
    }

    [Fact]
    public async Task TextTurn事件顺序_指令先于台词_并带真音频()
    {
        var client = Client();
        using var call = client.TextTurn(new TextRequest { Text = "前面路口左转" });

        var events = new List<TurnEvent>();
        await foreach (var e in call.ResponseStream.ReadAllAsync())
        {
            events.Add(e);
        }

        Assert.Equal(TurnEvent.PayloadOneofCase.AsrFinal, events[0].PayloadCase);

        int drive = events.FindIndex(e => e.PayloadCase == TurnEvent.PayloadOneofCase.DriveAction);
        int reply = events.FindIndex(e => e.PayloadCase == TurnEvent.PayloadOneofCase.LlmDelta);
        int tts = events.FindIndex(e => e.PayloadCase == TurnEvent.PayloadOneofCase.TtsAudio);

        Assert.True(drive >= 0 && drive < reply, "drive_action 必须先于台词");
        Assert.True(tts > reply, "tts_audio 应在台词之后");

        Assert.Equal(Core.Intents.TurnLeft, events[drive].DriveAction.Intent);
        Assert.True(events[tts].TtsAudio.FromVoicelib);
        Assert.Equal(24000, events[tts].TtsAudio.SampleRate);
        Assert.NotEmpty(events[tts].TtsAudio.Pcm16);
    }

    [Fact]
    public async Task SyncGameState后状态被采纳()
    {
        var client = Client();

        var ack = await client.SyncGameStateAsync(new GameState
        {
            Trust = 33f,
            Emotion = Emotion.Nervous,
            Telemetry = new VehicleTelemetry { SpeedKmh = 5f },
        });

        Assert.True(ack.Ok);

        // 低速下停车指令应被放行（场景规则使用的是同步过来的速度）
        using var call = client.TextTurn(new TextRequest { Text = "靠边停车" });
        var events = new List<TurnEvent>();
        await foreach (var e in call.ResponseStream.ReadAllAsync()) events.Add(e);

        var action = events.First(e => e.PayloadCase == TurnEvent.PayloadOneofCase.DriveAction).DriveAction;
        Assert.Equal(Core.Intents.Park, action.Intent);
    }

    [Fact]
    public async Task VoiceTurn在ASR缺失时返回明确错误()
    {
        var client = Client();
        using var call = client.VoiceTurn();

        await call.RequestStream.WriteAsync(new AudioChunk
        {
            Pcm16 = Google.Protobuf.ByteString.CopyFrom(new byte[3200]),
            EndOfStream = true,
        });
        await call.RequestStream.CompleteAsync();

        var events = new List<TurnEvent>();
        await foreach (var e in call.ResponseStream.ReadAllAsync()) events.Add(e);

        Assert.Contains(events, e => e.PayloadCase == TurnEvent.PayloadOneofCase.Error
                                     && e.Error.Code == "asr_unavailable");
    }
}