using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Core = Copilot.Core;
using Proto = Copilot.Contracts;

namespace Copilot.Client
{
    /// <summary>
    /// 一次语音回合的会话（<c>VoiceTurn</c> 双向流的客户端封装）。
    ///
    /// 用法：<c>WriteAsync</c> 推 PCM16 分块（16kHz 单声道），推完调 <c>CompleteRequestAsync</c>；
    /// 同时用 <c>ReadEventsAsync</c> 消费下行事件。**必须 Dispose**，否则流不会关闭。
    /// </summary>
    public sealed class VoiceTurnSession : IDisposable
    {
        private readonly AsyncDuplexStreamingCall<Proto.AudioChunk, Proto.TurnEvent> _call;
        private long _seq;
        private bool _disposed;

        internal VoiceTurnSession(AsyncDuplexStreamingCall<Proto.AudioChunk, Proto.TurnEvent> call)
        {
            _call = call ?? throw new ArgumentNullException(nameof(call));
        }

        /// <summary>上行一块 16kHz 单声道 PCM16。<paramref name="endOfStream"/> 置 true 表示本回合录音结束。</summary>
        public async Task WriteAsync(byte[] pcm16, bool endOfStream = false, CancellationToken ct = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(VoiceTurnSession));
            var chunk = new Proto.AudioChunk
            {
                Seq = _seq++,
                EndOfStream = endOfStream,
            };
            if (pcm16 != null && pcm16.Length > 0)
            {
                chunk.Pcm16 = Google.Protobuf.ByteString.CopyFrom(pcm16);
            }
            await _call.RequestStream.WriteAsync(chunk, ct).ConfigureAwait(false);
        }

        /// <summary>结束上行（半关闭）。之后仍可继续读下行事件，直到服务端结束流。</summary>
        public Task CompleteRequestAsync() => _call.RequestStream.CompleteAsync();

        /// <summary>消费下行事件流，直到服务端结束流。</summary>
        public async IAsyncEnumerable<Core.TurnEvent> ReadEventsAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            while (await _call.ResponseStream.MoveNext(ct).ConfigureAwait(false))
            {
                yield return ClientProtoMapper.ToDomain(_call.ResponseStream.Current);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _call.Dispose();
        }
    }
}