using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Copilot.Contracts;
using Google.Protobuf;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using Core = Copilot.Core;
using Proto = Copilot.Contracts;

namespace Copilot.Client
{
    /// <summary>
    /// 前端 ↔ AI 伴生进程的客户端（IT方案1.md V2.0 §4）。
    ///
    /// 这是方案里说的**共享接入层**：不依赖 UnityEngine，纯 netstandard2.1 + 托管实现
    /// （无各平台原生库），因此 Unity 脚本、编辑器工具、集成测试共用同一份代码。
    ///
    /// **传输用 gRPC-Web（HTTP/1.1）而不是原生 gRPC**：Unity 2022.3 的 Mono BCL 不支持 h2c，
    /// 原生 gRPC 实测直接失败（<c>Bad gRPC response. Response protocol downgraded to HTTP/1.1.</c>）。
    /// 让 .NET 侧也走 gRPC-Web 的好处是**测试跑的就是游戏实际用的那条传输路径**。
    /// 代价：gRPC-Web 不支持双向流，所以 <c>VoiceTurn</c> 是「单请求 + 服务端流」。
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

            Url = url;
            _channel = CreateChannel(url);
            _client = new AIService.AIServiceClient(_channel);
        }

        public CopilotClient(string host, int port)
            : this($"http://{host}:{port}")
        {
        }

        public string Url { get; }

        /// <summary>
        /// 按本项目传输约定建 channel：**gRPC-Web over HTTP/1.1**。
        /// 测试夹具也用它，避免「两处各配一遍、哪天漂移了都不知道」。
        ///
        /// 为什么必须显式把 HTTP 版本压到 1.1：
        /// Grpc.Net.Client 默认把请求标成 HTTP/2，在**明文**端点上会直接用 h2c 先验知识，
        /// 而服务端要求 HTTP/1.1 → 连接被 RST：
        /// <c>The HTTP/2 server closed the connection. HTTP/2 error code 'HTTP_1_1_REQUIRED' (0xd)</c>（实测）。
        /// gRPC-Web 本身就是 HTTP/1.1 协议，这里要说死，别指望协商。
        /// </summary>
        public static GrpcChannel CreateChannel(string url)
        {
            var handler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, new HttpClientHandler());
            return GrpcChannel.ForAddress(url, new GrpcChannelOptions
            {
                HttpHandler = handler,
                HttpVersion = new Version(1, 1),
            });
        }

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

        /// <summary>
        /// 语音回合：整段 16kHz 单声道 PCM16 上行 → 下行事件流。
        ///
        /// 注意这是「单请求 + 服务端流」，不是双向流 —— gRPC-Web 不支持双向流（见类注释）。
        /// </summary>
        public async IAsyncEnumerable<Core.TurnEvent> VoiceTurnAsync(
            byte[] pcm16,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var request = new Proto.AudioTurnRequest
            {
                Pcm16 = ByteString.CopyFrom(pcm16 ?? Array.Empty<byte>()),
            };

            using (var call = _client.VoiceTurn(request, cancellationToken: ct))
            {
                while (await call.ResponseStream.MoveNext(ct).ConfigureAwait(false))
                {
                    yield return ClientProtoMapper.ToDomain(call.ResponseStream.Current);
                }
            }
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