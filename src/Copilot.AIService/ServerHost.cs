using System.Net;
using Copilot.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Copilot.AIService;

/// <summary>
/// 本地 gRPC 宿主。
///
/// **同时支持 h2c 与 gRPC-Web**：Unity 2022.3 的 Mono BCL 只支持 HTTP/1.1，
/// 托管 gRPC 会拿到 "Response protocol downgraded to HTTP/1.1"，所以客户端一律走 gRPC-Web；
/// 这里保留 HTTP/1.1 + HTTP/2 双协议，.NET 侧用原生 h2c 也仍然能连（见开发进度.md 债 #13）。
/// </summary>
public static class ServerHost
{
    public static async Task RunAsync(AiRuntime runtime, CancellationToken ct = default)
    {
        var uri = new Uri(runtime.Config.Server.Url);
        int port = uri.Port > 0 ? uri.Port : 50051;

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();

        builder.WebHost.ConfigureKestrel(options =>
        {
            // gRPC-Web 走 HTTP/1.1，原生 gRPC 走 h2c —— 两者都留着
            options.ListenLocalhost(port, listen => listen.Protocols = HttpProtocols.Http1AndHttp2);
        });

        builder.Services.AddSingleton(runtime);
        builder.Services.AddGrpc();

        var app = builder.Build();
        app.UseGrpcWeb(new GrpcWebOptions { DefaultEnabled = true });
        app.MapGrpcService<GrpcAiService>();

        Console.WriteLine($"[server] gRPC/gRPC-Web 监听 http://127.0.0.1:{port}（llm={runtime.Llm.Name}, asr={runtime.Asr?.Name ?? "none"}, tts={runtime.Tts.Name}）");
        Console.Out.Flush();

        await app.RunAsync(ct);
    }
}