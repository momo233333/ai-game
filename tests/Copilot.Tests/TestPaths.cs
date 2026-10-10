using Copilot.AIService;
using Copilot.Core;

namespace Copilot.Tests;

/// <summary>测试期定位仓库资源（统一走 Config.FindRepoRoot，避免硬编码盘符）。</summary>
internal static class TestPaths
{
    public static string RepoRoot =>
        Config.FindRepoRoot() ?? throw new InvalidOperationException("找不到仓库根（应含 voice-corpus 目录）");

    public static string VoiceLibManifest => Path.Combine(RepoRoot, "voice-corpus", "manifest.json");

    public static string SampleWav(string id) => Path.Combine(RepoRoot, "voice-corpus", "wavs", id + ".wav");

    public static VoiceLib LoadVoiceLib() => VoiceLib.Load(VoiceLibManifest);
}