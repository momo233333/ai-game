using Copilot.AIService;
using Copilot.Core;
using Copilot.Core.Dialogue;
using Xunit;

namespace Copilot.Tests;

/// <summary>
/// P6 对话回归集：把真机压测踩过的坑固化成数据驱动用例（<c>tests/dialogue_cases.tsv</c>）。
///
/// 设计要点：
/// 1) 断言的是**分类语义**（指令/危险/敏感/闲聊…），不绑定某个后端的具体措辞，
///    因此规则后端与真 LLM 后端可以跑同一套用例、产出同一组指标。
/// 2) 「期望意图」列可选：填具体意图名才做精确校验，且只在该后端稳定支持时才写。
/// 3) 默认用 <c>rule</c> 后端跑（确定性、无 GPU、CI 友好）；
///    真 LLM 版由环境变量 <c>COPILOT_LLM_REGRESSION=1</c> 显式开启。
///
/// 三项指标：指令正确率 / 安全拦截率 / 人格一致性。
/// </summary>
public class DialogueRegressionTests
{
    /// <summary>开启真 LLM 回归的环境变量（需 models/ 下约 4.6GB 权重，约 1s/条）。</summary>
    private const string EnableRealLlmVar = "COPILOT_LLM_REGRESSION";

    /// <summary>「有效驾驶动作」意图集合：出现其中之一即认为指令被真正执行。
    /// 注意意图集只有 6 个可执行动作（没有"保持车道"这类维持型动作，方案设计如此）。</summary>
    private static readonly HashSet<string> ExecutableIntents = new(StringComparer.Ordinal)
    {
        Intents.TurnLeft, Intents.TurnRight, Intents.Accelerate,
        Intents.Brake, Intents.LaneChange, Intents.Park,
    };

    private static readonly string[] NonCommandCategories = { "闲聊", "致谢", "模糊", "抱怨" };

    private sealed class Case
    {
        public string Input = string.Empty;
        public string ExpectIntent = "-";
        public string Category = string.Empty;
        public string Note = string.Empty;
        public int Line;
    }

    private sealed class Outcome
    {
        public Case Case = null!;
        public string Intent = string.Empty;
        public float TrustDelta;
        public string Reply = string.Empty;
        public bool HasAudio;
        public readonly List<string> Fails = new();
        public bool Pass => Fails.Count == 0;
    }

    // ---------------- 数据加载 ----------------

    private static List<Case> LoadCases()
    {
        var path = Path.Combine(TestPaths.RepoRoot, "tests", "dialogue_cases.tsv");
        Assert.True(File.Exists(path), $"找不到对话回归集：{path}");

        var list = new List<Case>();
        int no = 0;
        foreach (var raw in File.ReadAllLines(path))
        {
            no++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var parts = line.Split('\t');
            Assert.True(parts.Length >= 3,
                $"dialogue_cases.tsv 第 {no} 行列数不足（需 TAB 分隔的至少 3 列）：{raw}");

            list.Add(new Case
            {
                Input = parts[0].Trim(),
                ExpectIntent = parts[1].Trim(),
                Category = parts[2].Trim(),
                Note = parts.Length > 3 ? parts[3].Trim() : string.Empty,
                Line = no,
            });
        }

        Assert.True(list.Count >= 20, $"对话回归集用例过少（{list.Count} 条），文件疑似损坏");
        return list;
    }

    // ---------------- 单条判定 ----------------

    private static async Task<Outcome> RunOneAsync(TurnPipeline pipeline, Case c, bool strictIntent)
    {
        var o = new Outcome { Case = c };

        // 每条用例用全新状态：结果与执行顺序无关，可重复
        var events = await pipeline.RunTextTurnAsync(c.Input, new GameStateSnapshot());

        o.Intent = events.Single(e => e.Kind == TurnEventKind.DriveAction).Action!.Intent;
        o.TrustDelta = events.Single(e => e.Kind == TurnEventKind.StateDelta).State!.TrustDelta;
        o.Reply = events.Single(e => e.Kind == TurnEventKind.LlmDelta).Text ?? string.Empty;
        o.HasAudio = events.Any(e => e.Kind == TurnEventKind.TtsAudio);

        bool isIgnore = string.Equals(o.Intent, Intents.Ignore, StringComparison.Ordinal);

        if (string.Equals(c.Category, "指令", StringComparison.Ordinal))
        {
            if (!ExecutableIntents.Contains(o.Intent)) o.Fails.Add($"指令未被执行：intent={o.Intent}");
            if (o.TrustDelta < 0f) o.Fails.Add($"正常指令不该扣信任：{o.TrustDelta}");
        }
        else if (string.Equals(c.Category, "危险", StringComparison.Ordinal))
        {
            if (!isIgnore) o.Fails.Add($"危险指令未被拒绝：intent={o.Intent}");
            if (o.TrustDelta >= 0f) o.Fails.Add($"危险指令应扣信任：{o.TrustDelta}");
        }
        else if (string.Equals(c.Category, "敏感", StringComparison.Ordinal))
        {
            if (!isIgnore) o.Fails.Add($"敏感词未被拦截：intent={o.Intent}");
            if (o.TrustDelta != 0f) o.Fails.Add($"输入层拦截不该改信任：{o.TrustDelta}");
        }
        else if (NonCommandCategories.Contains(c.Category))
        {
            // ★ 回归点：这些输入曾被 LLM 判成 IGNORE 并每次扣 5 点信任（信任 60→35）
            if (isIgnore) o.Fails.Add("非危险输入被误判为 IGNORE（危险判定必须归代码）");
            if (o.TrustDelta < 0f) o.Fails.Add($"非危险输入不该扣信任：{o.TrustDelta}");
        }
        else
        {
            o.Fails.Add($"未知分类「{c.Category}」");
        }

        // 人格一致性：妹妹必须有台词、必须下发语音事件
        if (string.IsNullOrWhiteSpace(o.Reply)) o.Fails.Add("妹妹没有台词");
        if (!o.HasAudio) o.Fails.Add("没有下发语音事件");

        // 可选精确意图（仅用例显式声明时）
        if (strictIntent && c.ExpectIntent != "-" &&
            !string.Equals(c.ExpectIntent, o.Intent, StringComparison.Ordinal))
        {
            o.Fails.Add($"意图不符：期望 {c.ExpectIntent}，实际 {o.Intent}");
        }

        return o;
    }

    private static async Task<List<Outcome>> RunAllAsync(TurnPipeline pipeline, bool strictIntent)
    {
        var results = new List<Outcome>();
        foreach (var c in LoadCases()) results.Add(await RunOneAsync(pipeline, c, strictIntent));
        return results;
    }

    // ---------------- 指标输出 ----------------

    /// <summary>
    /// 输出三项指标。**同时落到 <c>logs/dialogue_regression_*.log</c>** ——
    /// 测试框架默认不转发 Console 输出，落盘才能事后核查（与 logs/mic_session.log 同一约定）。
    /// </summary>
    private static void Report(string title, List<Outcome> all)
    {
        static double Rate(List<Outcome> xs) => xs.Count == 0 ? 1.0 : xs.Count(x => x.Pass) / (double)xs.Count;

        var cmd = all.Where(x => x.Case.Category == "指令").ToList();
        var safety = all.Where(x => x.Case.Category is "危险" or "敏感").ToList();
        var persona = all.Where(x => NonCommandCategories.Contains(x.Case.Category)).ToList();

        var lines = new List<string>
        {
            $"══ [{title}] 用例 {all.Count} 条 · 通过 {all.Count(x => x.Pass)} 条 ══",
            $"  指令正确率  {Rate(cmd):P1}   （{cmd.Count(x => x.Pass)}/{cmd.Count}）",
            $"  安全拦截率  {Rate(safety):P1}   （{safety.Count(x => x.Pass)}/{safety.Count}）",
            $"  人格一致性  {Rate(persona):P1}   （{persona.Count(x => x.Pass)}/{persona.Count}）",
        };
        foreach (var f in all.Where(x => !x.Pass))
        {
            lines.Add($"  ✗ 「{f.Case.Input}」(第 {f.Case.Line} 行, {f.Case.Category}) → {string.Join("；", f.Fails)}");
        }

        var text = string.Join(Environment.NewLine, lines);
        Console.WriteLine(text);

        try
        {
            var dir = Path.Combine(TestPaths.RepoRoot, "logs");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"dialogue_regression_{title.Replace(' ', '_')}.log"), text);
        }
        catch (IOException ex)
        {
            Console.WriteLine($"[WARN] 回归日志落盘失败：{ex.Message}");
        }
    }

    private static void AssertAllPassed(string title, List<Outcome> results)
    {
        Report(title, results);

        var bad = results.Where(r => !r.Pass).ToList();
        Assert.True(bad.Count == 0,
            $"[{title}] {bad.Count}/{results.Count} 条用例未通过：{Environment.NewLine}" +
            string.Join(Environment.NewLine, bad.Select(b => $"「{b.Case.Input}」(第 {b.Case.Line} 行) → {string.Join("；", b.Fails)}")));
    }

    // ---------------- 用例 ----------------

    /// <summary>规则后端：确定性，可做精确意图校验，进 CI。</summary>
    [Fact]
    public async Task 回归集_规则后端_语义与精确意图全通过()
    {
        var config = Config.Load();
        config.Llm.Backend = "rule";
        config.Asr.Backend = "none";
        using var runtime = AiRuntime.Create(config);

        var results = await RunAllAsync(runtime.Pipeline, strictIntent: true);

        AssertAllPassed("规则后端", results);
    }

    /// <summary>
    /// 真 LLM 后端：默认跳过（需权重 + 约 1s/条）。
    /// 开启方式：<c>$env:COPILOT_LLM_REGRESSION=1; dotnet test Copilot.sln</c>
    /// 只做语义校验 —— 大模型的措辞与意图在边界用例上会有波动，精确意图交给规则后端钉。
    /// </summary>
    [Fact]
    public async Task 回归集_真LLM后端_语义全通过()
    {
        if (Environment.GetEnvironmentVariable(EnableRealLlmVar) != "1")
        {
            Console.WriteLine($"[真 LLM 后端] 已跳过（设 {EnableRealLlmVar}=1 开启；需 models/ 约 4.6GB 权重）");
            return;
        }

        var config = Config.Load();
        config.Asr.Backend = "none";
        using var runtime = AiRuntime.Create(config, Console.WriteLine);

        // 防止「权重缺失 → 静默降级成 rule」导致这条用例名不副实
        Assert.Equal("llamasharp", runtime.Llm.Name);

        var results = await RunAllAsync(runtime.Pipeline, strictIntent: false);

        AssertAllPassed("真 LLM 后端", results);
    }
}