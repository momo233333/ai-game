using System.Net;
using Copilot.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Copilot.AIService;

/// <summary>本地 gRPC 宿主（localhost h2c，无需 TLS）。</summary>
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
            options.ListenLocalhost(port, listen => listen.Protocols = HttpProtocols.Http2);
        });

        builder.Services.AddSingleton(runtime);
        builder.Services.AddGrpc();

        var app = builder.Build();
        app.MapGrpcService<GrpcAiService>();

        Console.WriteLine($"[server] gRPC 监听 http://127.0.0.1:{port}（llm={runtime.Llm.Name}, asr={runtime.Asr?.Name ?? "none"}, tts={runtime.Tts.Name}）");
        Console.Out.Flush();

        await app.RunAsync(ct);
    }
}