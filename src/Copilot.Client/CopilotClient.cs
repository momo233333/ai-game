using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Copilot.Contracts;
using Grpc.Net.Client;
using Core = Copilot.Core;
using Proto = Copilot.Contracts;

namespace Copilot.Client
{
    /// <summary>
    /// 前端 ↔ AI 伴生进程的 gRPC 客户端（IT方案1.md V2.0 §4）。
    ///
    /// 这是方案里说的**共享接入层**：不依赖 UnityEngine，纯 netstandard2.1 + 托管 gRPC
    /// （无各平台原生库），因此 Unity 脚本、编辑器工具、集成测试共用同一份代码。
    ///
    /// 下行事件统一映射回 <see cref="Core.TurnEvent"/>，前端不需要认识 protobuf。
    /// </summary>
    public sealed class CopilotClient : IDisposable
    {
        private readonly GrpcChannel _channel;
        private readonly AIService.AIServiceClient _client;

        /// <summary>连到伴生进程（默认 <c>http://127.0.0.1:50051</c>）。</summary>
        public CopilotClient(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("url 不能为空", nameof(url));

            // h2c（明文 HTTP/2）。.NET Core 3.x 需要这个开关，.NET 5+ 无需；重复设置无害。
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

            Url = url;
            _channel = GrpcChannel.ForAddress(url);
            _client = new AIService.AIServiceClient(_channel);
        }

        public CopilotClient(string host, int port)
            : this($"http://{host}:{port}")
        {
        }

        public string Url { get; }

        /// <summary>健康检查 + 各后端实际生效的降级状态。</summary>
        public async Task<Proto.Health> HealthAsync(CancellationToken ct = default)
        {
            return await _client.PingAsync(new Proto.Empty(), cancellationToken: ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 文本回合（ASR 未就绪或调试入口）。事件流顺序与管线一致：
        /// asr_final → drive_action → llm_delta → tts_audio → state_delta。
        /// </summary>
        public async IAsyncEnumerable<Core.TurnEvent> TextTurnAsync(
            string text,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            using (var call = _client.TextTurn(new Proto.TextRequest { Text = text ?? string.Empty }, cancellationToken: ct))
            {
                while (await call.ResponseStream.MoveNext(ct).ConfigureAwait(false))
                {
                    yield return ClientProtoMapper.ToDomain(call.ResponseStream.Current);
                }
            }
        }

        /// <summary>语音回合：上行 PCM16 分块、下行事件流（双向流式）。用完必须 Dispose。</summary>
        public VoiceTurnSession StartVoiceTurn(CancellationToken ct = default)
        {
            return new VoiceTurnSession(_client.VoiceTurn(cancellationToken: ct));
        }

        /// <summary>回推权威游戏状态（信任/情绪/遥测）。</summary>
        public async Task<bool> SyncGameStateAsync(Core.GameStateSnapshot state, CancellationToken ct = default)
        {
            var ack = await _client
                .SyncGameStateAsync(ClientProtoMapper.ToProto(state), cancellationToken: ct)
                .ConfigureAwait(false);
            return ack.Ok;
        }

        public void Dispose() => _channel?.Dispose();
    }
}