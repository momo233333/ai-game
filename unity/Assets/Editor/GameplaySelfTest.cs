using System;
using System.Collections.Generic;
using System.Text;
using Copilot.Audio;
using Copilot.Core;
using Copilot.Core.Gameplay;
using Copilot.Core.Levels;
using Copilot.Gameplay;
using Copilot.UI;
using Copilot.Vehicle;
using UnityEditor;
using UnityEngine;

namespace Copilot.EditorTools
{
    /// <summary>
    /// 玩法自检：**不靠人手玩**，在 Unity 里程序化走完一整局并断言结果。
    ///
    /// 为什么必须有这个：`SceneBuilder.Validate` 只能证明"引用连上了"，Core 单测只能证明"规则对了"
    /// —— 两者都覆盖不到「Unity 这一层真的把失误注入给车、把玩家干预对上窗口、结算出正确的分」。
    ///
    /// 做法：**编辑模式下用 `AddComponent` 造一套组件**（注意：`AddComponent` **不保证**触发 `Awake`，
    /// 所以 `MistakeRunner` / `TutorialDirector` 都做了懒初始化，字段注入后 `Rebind()` 才算连上），
    /// 然后手动喂合成上下文推进 —— 靠 `MistakeRunner.Advance` / `SubmitPlayerIntent` 两个公开接缝。
    /// 因为线上默认**关掉了主动犯错**（`mistakesEnabled=false`），自检里必须显式打开才验得了结算。
    ///
    /// 用法：
    /// ① 批处理：`powershell -File tools\unity_batch.ps1 -Method Copilot.EditorTools.GameplaySelfTest.RunAndExit`
    /// ② 编辑器菜单：`副驾指令 / 玩法自检（通关）`
    /// </summary>
    public static class GameplaySelfTest
    {
        [MenuItem("副驾指令/玩法自检（通关）")]
        public static void RunFromMenu()
        {
            Run();
        }

        /// <summary>批处理入口：跑完用返回码退出（0 = 通过）。</summary>
        public static void RunAndExit()
        {
            int code = Run();
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        public static int Run()
        {
            var problems = new StringBuilder();
            var log = new StringBuilder();

            try
            {
                DriveFullRun(problems, log);
            }
            catch (Exception ex)
            {
                problems.Append("\n  ✗ 异常：").Append(ex);
            }

            if (problems.Length > 0)
            {
                Debug.LogError("[GameplaySelfTest] ✗ 失败：" + problems);
                return 1;
            }

            Debug.Log("[GameplaySelfTest] 通过：\n" + log);
            return 0;
        }

        static void DriveFullRun(StringBuilder problems, StringBuilder log)
        {
            var city = new GameObject("SelfTestCity");
            var car = new GameObject("SelfTestCar");
            var rig = new GameObject("SelfTestRig");

            try
            {
                // ---- 组装：道路几何（City）→ 传感器（Car）→ 玩法驱动（Rig）----
                // 注意 MistakeRunner/TutorialDirector/VehicleController 的 Awake 在这里就会跑
                var road = city.AddComponent<RoadGeometry>();
                var vehicle = car.AddComponent<VehicleController>();     // [RequireComponent] 会自动补 Rigidbody
                var sensors = car.AddComponent<VehicleSensors>();
                sensors.vehicle = vehicle;
                sensors.road = road;

                var executor = rig.AddComponent<ActionExecutor>();
                executor.vehicle = vehicle;

                var tutorial = rig.AddComponent<TutorialDirector>();

                var runner = rig.AddComponent<MistakeRunner>();
                runner.executor = executor;
                runner.vehicle = vehicle;
                runner.sensors = sensors;
                runner.tutorial = tutorial;
                runner.mistakesEnabled = true;   // 线上默认关；自检要验失误剧本与结算，必须显式打开

                var outcomes = new List<MistakeOutcome>();
                runner.MistakeResolved += o => outcomes.Add(o);

                // ---- ① 结构 ----
                Require(road.laneCenterXs != null && road.laneCenterXs.Length == 4,
                    "RoadGeometry 不是 4 条车道", problems);
                Require(tutorial.Progress != null, "TutorialDirector.Progress 为 null（Awake 未建）", problems);
                Require(runner.Session != null, "MistakeRunner.Session 为 null（Awake 未建）", problems);
                Require(LevelCatalog.Subject1Tutorial.Steps.Count == 6, "教学关不是六步", problems);

                // ---- ② 教学六步 ----
                int advancedCount = 0;
                foreach (var step in LevelCatalog.Subject1Tutorial.Steps)
                {
                    if (tutorial.Progress.Observe(step.AcceptIntents[0])) advancedCount++;
                }
                Require(advancedCount == 6, "六步只推进了 " + advancedCount + " 步", problems);
                Require(tutorial.Progress.IsCompleted, "六步走完却不是完成态", problems);
                log.Append("  教学：").Append(tutorial.Progress.StepIndex).Append('/')
                   .Append(tutorial.Progress.TotalSteps).AppendLine(" 步");

                // ---- ③ 失误①开演 → 及时干预救回 ----
                // 上下文：车速够、已完成 9 步（远超触发条件）、车道几何有效
                var tick = runner.Advance(100f, Ctx(25f, 9, lateralOffsetM: 0f, laneGeometryValid: true));

                Require(tick.Opened, "失误①没有开演", problems);
                Require(runner.Current != null && runner.Current.Id == MistakeCatalog.DriftOutOfLane.Id,
                    "开演的不是「偏出车道」，而是 " + (runner.Current != null ? runner.Current.Id : "null"), problems);
                Require(tick.InjectNow && tick.InjectAction != null, "开演那一帧没有注入犯错动作", problems);

                // 这一条是真正的集成断言：注入真的走到了 ActionExecutor
                Require(executor.Current != null && executor.Current.Intent == Intents.LaneChange,
                    "犯错动作没有真的注入到 ActionExecutor", problems);

                Require(runner.Phase == MistakePhase.Warning, "开演后不是 Warning 阶段", problems);
                bool saved = runner.SubmitPlayerIntent(Intents.LaneChange);
                Require(saved, "窗口内说了算救的指令却没被判为救回", problems);
                Require(runner.Phase == MistakePhase.Idle, "救回后没有回到 Idle", problems);
                Require(runner.Session.Saves == 1, "Saves 不是 1，而是 " + runner.Session.Saves, problems);
                Require(outcomes.Count == 1 && outcomes[0].Saved, "救回没有产出 Saved 结果", problems);
                Require(outcomes.Count == 1 && outcomes[0].LocalEventName == string.Empty,
                    "救回不该产生本地事件（不扣信任）", problems);

                // ---- ④ 失误②开演 → 窗口耗尽 = 漏掉 ----
                runner.Advance(8500f, Ctx(25f, 9, laneGeometryValid: true));   // 过 8s 冷却
                Require(runner.Current != null && runner.Current.Id == MistakeCatalog.ForgotToBrake.Id,
                    "第二个失误没有按剧本顺序开演", problems);

                runner.Advance(2700f, Ctx(25f, 9, laneGeometryValid: false));   // 2600ms 窗口耗尽
                Require(runner.Phase == MistakePhase.Idle, "窗口耗尽后没有结案", problems);
                Require(runner.Session.Misses == 1, "Misses 不是 1，而是 " + runner.Session.Misses, problems);
                Require(outcomes.Count == 2 && !outcomes[1].Saved, "漏掉没有产出失败结果", problems);
                Require(outcomes.Count == 2 && outcomes[1].LocalEventName == MistakeCatalog.ForgotToBrake.FailLocalEvent,
                    "失败事件名不是 hard_brake，而是 " + (outcomes.Count == 2 ? outcomes[1].LocalEventName : "?"), problems);
                log.Append("  失误：救回 ").Append(runner.Session.Saves)
                   .Append(" · 漏掉 ").Append(runner.Session.Misses).AppendLine();

                // ---- ⑤ 物理失败路径：真偏出去 → lane_departure ----
                runner.RestartLevel();
                Require(runner.Session.Saves == 0 && runner.Session.Misses == 0, "重开后计数没有清零", problems);
                Require(tutorial.Progress.StepIndex == 0, "重开后教学进度没有回到第一步", problems);

                outcomes.Clear();
                runner.Advance(100f, Ctx(25f, 9, laneGeometryValid: true));                 // 开演
                runner.Advance(100f, Ctx(25f, 9, lateralOffsetM: 1.5f));                    // 越线 100ms
                runner.Advance(100f, Ctx(25f, 9, lateralOffsetM: 1.5f));                    // 200ms
                runner.Advance(100f, Ctx(25f, 9, lateralOffsetM: 1.5f));                    // 300ms → 结案
                Require(outcomes.Count == 1 && outcomes[0].Kind == MistakeOutcomeKind.FailedByPhysics,
                    "越线持续足够久却没有判物理失败", problems);
                Require(outcomes.Count == 1 && outcomes[0].LocalEventName == "lane_departure",
                    "物理失败事件名不是 lane_departure", problems);

                // ---- ⑥ 权威一局：六步 + 失误①救回 + 失误②漏掉 → 结算 ----
                runner.RestartLevel();
                outcomes.Clear();

                foreach (var step in LevelCatalog.Subject1Tutorial.Steps)
                {
                    tutorial.Progress.Observe(step.AcceptIntents[0]);
                }
                runner.Session.ObserveStepAdvance(tutorial.Progress.StepIndex);   // Update 里那行同步逻辑

                runner.Advance(100f, Ctx(25f, 9));                                  // 失误①开演
                runner.SubmitPlayerIntent(Intents.LaneChange);                      // 救回
                runner.Advance(8500f, Ctx(25f, 9));                                 // 过冷却 → 失误②开演
                runner.Advance(2700f, Ctx(25f, 9, laneGeometryValid: false));       // 漏掉

                var report = runner.EndLevel();
                Require(report.SafetyPoints == LevelSession.SafetyFull,
                    "安全分不是 40，而是 " + report.SafetyPoints, problems);
                Require(report.InterventionPoints == 20,
                    "干预分不是 20，而是 " + report.InterventionPoints, problems);
                Require(report.TutorialPoints == LevelSession.TutorialFull,
                    "教学分不是 20，而是 " + report.TutorialPoints, problems);
                Require(report.Score == 80, "总分不是 80，而是 " + report.Score, problems);
                Require(report.Stars == 2, "星级不是 2 星，而是 " + report.Stars, problems);

                log.Append("  结算：安全 ").Append(report.SafetyPoints)
                   .Append(" · 干预 ").Append(report.InterventionPoints)
                   .Append(" · 教学 ").Append(report.TutorialPoints)
                   .Append(" → 总分 ").Append(report.Score)
                   .Append(" · ").Append(report.Stars).AppendLine(" 星");

                // ---- ⑦ 本地语音库：玩法台词必须真的能从 Unity 里读出来 ----
                // 这一段覆盖的是「她慌起来要听得见」这条路：仓库根查找 → manifest → wav → PCM。
                // Core 单测只能证明 id 与字幕文本一致，证明不了 Unity 侧真的读得到音频文件。
                var localVoice = rig.AddComponent<LocalVoiceLib>();
                Require(localVoice.IsReady, "本地语音库未就绪（仓库根查找或 manifest 解析失败）", problems);

                int voiceCues = 0;
                foreach (var def in MistakeCatalog.Subject1Tutorial)
                {
                    RequireVoice(localVoice, def.Panic.Id, problems);
                    RequireVoice(localVoice, def.Saved.Id, problems);
                    RequireVoice(localVoice, def.Failed.Id, problems);
                    voiceCues += 3;
                }
                log.Append("  本地语音：就绪，玩法台词 ").Append(voiceCues).AppendLine(" 条可读");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(rig);
                UnityEngine.Object.DestroyImmediate(car);
                UnityEngine.Object.DestroyImmediate(city);
            }
        }

        static MistakeContext Ctx(float speedKmh, int completedSteps,
            float lateralOffsetM = 0f, bool laneGeometryValid = true)
        {
            return new MistakeContext
            {
                ElapsedSeconds = 60f,
                CompletedSteps = completedSteps,
                DistanceTravelledM = 100f,
                SpeedKmh = speedKmh,
                LateralOffsetM = lateralOffsetM,
                LaneGeometryValid = laneGeometryValid,
                DistanceAheadToHazardM = float.PositiveInfinity,
            };
        }

        /// <summary>这条语音 id 是否真的能读出 PCM（查 manifest 条目 + 读 wav）。</summary>
        static void RequireVoice(LocalVoiceLib lib, string id, StringBuilder problems)
        {
            if (!lib.IsReady)
            {
                problems.Append("\n  ✗ 本地语音库未就绪，无法校验 ").Append(id);
                return;
            }

            var entry = lib.Library.FindById(id);
            if (entry == null)
            {
                problems.Append("\n  ✗ 本地语音库里没有 ").Append(id);
                return;
            }

            var pcm = lib.Library.Pcm16(entry);
            if (pcm == null || pcm.Length == 0)
            {
                problems.Append("\n  ✗ ").Append(id).Append(" 的 wav 读不出 PCM");
            }
        }

        static void Require(bool condition, string message, StringBuilder problems)
        {
            if (!condition) problems.Append("\n  ✗ ").Append(message);
        }
    }
}
