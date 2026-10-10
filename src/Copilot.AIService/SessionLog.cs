namespace Copilot.AIService;

/// <summary>
/// 会话日志（IT方案1.md 阶段 A 的自测取证）。
/// 麦克风交互是「跑起来才知道」的场景：把按下/抬起、每帧电平、partial 字幕、
/// 最终文本、事件流与错误全部落盘，跑一次就能拿日志定位问题，不必盯着控制台。
/// <c>AutoFlush=true</c>：即使进程卡死/被强杀，日志也已经在磁盘上。
/// </summary>
public sealed class SessionLog : IDisposable
{
    private readonly StreamWriter _writer;

    public string Path { get; }

    private SessionLog(string path, StreamWriter writer)
    {
        Path = path;
        _writer = writer;
    }

    /// <summary>打开日志；失败时返回 null（不能因为日志把主流程搞挂）。</summary>
    public static SessionLog? Open(string path)
    {
        try
        {
            var full = System.IO.Path.GetFullPath(path);
            var dir = System.IO.Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var writer = new StreamWriter(full, append: false, System.Text.Encoding.UTF8) { AutoFlush = true };
            return new SessionLog(full, writer);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[log] 无法写日志 {path}：{ex.Message}");
            return null;
        }
    }

    public void Write(string tag, string message)
        => _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{tag,-6}] {message}");

    public void Dispose()
    {
        try
        {
            _writer.Dispose();
        }
        catch (Exception)
        {
            // 忽略关闭失败
        }
    }
}