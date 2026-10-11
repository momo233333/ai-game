using Copilot.Contracts;
using Core = Copilot.Core;
using Grpc.Core;
using GrpcService = Copilot.Contracts.AIService;

namespace Copilot.AIService;

/// <summary>
/// gRPC 服务端实现（IT方案1.md V2.0 §4.1）。
/// 四类 RPC：Ping（健康/降级状态）、SyncGameState、TextTurn、VoiceTurn。
/// </summary>
public sealed class GrpcAiService : GrpcService.AIServiceBase
{
    private readonly AiRuntime _runtime;

    public GrpcAiService(AiRuntime runtime) => _runtime = runtime;

    public override Task<Health> Ping(Empty request, ServerCallContext context)
    {
        return Task.FromResult(new Health
        {
            Ok = true,
            LlmBackend = _runtime.Llm.Name,
            AsrBackend = _runtime.Asr?.Name ?? "none",
            TtsBackend = _runtime.Tts.Name,
            Version = AiRuntime.Version,
        });
    }

    public override Task<Ack> SyncGameState(GameState request, ServerCallContext context)
    {
        _runtime.UpdateState(ProtoMapper.ToDomain(request));
        return Task.FromResult(new Ack { Ok = true });
    }

    public override async Task TextTurn(TextRequest request, IServerStreamWriter<TurnEvent> responseStream,
        ServerCallContext context)
    {
        // 直接转发流：drive_action 在 LLM 刚生成完 action 时就会推给客户端，不必等整段台词
        await foreach (var e in _runtime.Pipeline.RunTextTurnStreamAsync(
                           request.Text ?? string.Empty, _runtime.CurrentState, context.CancellationToken))
        {
            await responseStream.WriteAsync(ProtoMapper.ToProto(e));
        }
    }

    public override async Task VoiceTurn(AudioTurnRequest request, IServerStreamWriter<TurnEvent> responseStream,
        ServerCallContext context)
    {
        if (_runtime.Asr == null)
        {
            await responseStream.WriteAsync(ProtoMapper.ToProto(
                Core.TurnEvent.Error("asr_unavailable", "ASR 未就绪：缺少 sherpa-onnx 流式模型")));
            return;
        }

        var pcm = request.Pcm16?.ToByteArray() ?? Array.Empty<byte>();
        var text = await _runtime.Asr.TranscribeAsync(pcm, context.CancellationToken);

        await foreach (var e in _runtime.Pipeline.RunTextTurnStreamAsync(
                           text, _runtime.CurrentState, context.CancellationToken))
        {
            await responseStream.WriteAsync(ProtoMapper.ToProto(e));
        }
    }
}