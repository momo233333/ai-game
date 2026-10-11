using System.Text;
using Copilot.AIService.Audio;

namespace Copilot.AIService;

/// <summary>
/// 入口：默认交互式语音交互自测；--server 起 gRPC 服务（供 Unity 接入，阶段 C）。
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // 重定向到管道时可能不支持设置编码，忽略即可
        }

        if (HasFlag(args, "--help") || HasFlag(args, "-h"))
        {
            PrintUsage();
            return 0;
        }

        var config = Config.Load(GetOption(args, "--config"));

        // --device <n> 覆盖录音设备（配合 --devices 一起用）
        var deviceArg = GetOption(args, "--device");
        if (deviceArg != null && int.TryParse(deviceArg, out int deviceNumber))
        {
            config.Audio.DeviceNumber = deviceNumber;
        }

        if (HasFlag(args, "--devices"))
        {
            Console.WriteLine("录音设备（下标即 --device 的取值）：");
            foreach (var d in AudioIo.ListInputDevices()) Console.WriteLine("  " + d);
            Console.WriteLine($"当前配置：--device {config.Audio.DeviceNumber}");
            return 0;
        }

        var loadWatch = System.Diagnostics.Stopwatch.StartNew();
        using var runtime = AiRuntime.Create(config, Console.WriteLine);
        loadWatch.Stop();
        Console.WriteLine($"[启动] 后端装配（含模型加载）{loadWatch.ElapsedMilliseconds} ms");

        Cli.PrintBanner(runtime);

        if (HasFlag(args, "--server"))
        {
            await ServerHost.RunAsync(runtime);
            return 0;
        }

        if (HasFlag(args, "--health"))
        {
            Console.WriteLine($"  llm: {runtime.LlmDetail}");
            Console.WriteLine($"  asr: {runtime.AsrDetail}");
            Console.WriteLine($"  vad: {runtime.VadDetail}");
            Console.WriteLine($"  tts: {runtime.TtsDetail}");
            return 0;
        }

        var text = GetOption(args, "--text");
        if (text != null)
        {
            await Cli.RunTextAsync(runtime, text);
            return 0;
        }

        var play = GetOption(args, "--play");
        if (play != null)
        {
            Cli.RunPlay(runtime, play);
            return 0;
        }

        if (HasFlag(args, "--synth-voicelib"))
        {
            Cli.RunSynthVoiceLib(runtime);
            return 0;
        }

        var wav = GetOption(args, "--wav");
        if (wav != null)
        {
            await Cli.RunWavAsync(runtime, wav);
            return 0;
        }

        var vadCheck = GetOption(args, "--vad");
        if (vadCheck != null)
        {
            Cli.RunVadCheck(runtime, vadCheck);
            return 0;
        }

        if (HasFlag(args, "--mic"))
        {
            await Cli.RunMicAsync(runtime, HasFlag(args, "--speak"), HasFlag(args, "--free"),
                GetOption(args, "--dump-audio"), GetOption(args, "--log"));
            return 0;
        }

        await Cli.RunInteractiveAsync(runtime);
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
        用法：Copilot.AIService [选项]

          （无选项）            交互式文本对话（默认）
          --text "<指令>"        单条指令端到端
          --wav <文件>           音频文件 → ASR → 指令闭环
          --mic                  麦克风**按住空格说话**（松开即结束）→ ASR → 指令闭环
          --free                 配合 --mic：改为**免提**（VAD 自动断句，直接说话停顿即结束）
          --devices              列出录音设备下标后退出（选错设备会录到静音 → 识别出幻觉文本）
          --device <n>           指定录音设备下标（默认 0 = 系统默认）
          --dump-audio <wav>     配合 --mic：把每次按住录到的音频存成 wav（听录音判断是采集还是识别问题）
          --log <文件>           配合 --mic：会话日志路径（默认 logs\mic_session.log；记录按键/电平/字幕/事件/错误）
          --speak                配合 --wav/--mic：把妹妹的语音放出来
          --vad <文件>            VAD 诊断：报告语音帧占比 / 语音突起次数 / 检出句尾（无麦克风也能验）
          --play "<台词>"        直接让妹妹说一句（验证音频输出链路）
          --synth-voicelib       用当前实时合成引擎**重录** voice-corpus 的 71 条台词（保证与自由句同嗓门）
          --server               启动本地 gRPC 服务（供 Unity 接入）
          --health               打印后端装配与降级状态后退出
          --config <文件>        指定 appsettings.json
          -h, --help             显示本帮助
        """);
    }

    private static bool HasFlag(string[] args, string name)
        => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>支持 --key value 与 --key=value 两种写法。</summary>
    private static string? GetOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1 < args.Length ? args[i + 1] : string.Empty;
            }

            var prefix = name + "=";
            if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return args[i].Substring(prefix.Length);
            }
        }
        return null;
    }
}