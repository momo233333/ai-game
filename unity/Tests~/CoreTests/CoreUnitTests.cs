using System;
using System.Collections.Generic;
using Copilot.Core;

namespace Copilot.Tests
{
    public static class CoreUnitTests
    {
        // ---------- SimpleJson ----------

        [Test]
        public static void Json_ParsesNestedEvent()
        {
            string line = "{\"kind\":\"drive_action\",\"data\":{\"steering\":-0.5,\"hold_ms\":600,\"intent\":\"TURN_LEFT\",\"refused\":false,\"note\":\"左转\"}}";
            var evt = TurnEvent.Parse(line);
            Assert.Equal("drive_action", evt.Kind);
            Assert.Nearly(-0.5, (double)evt.Data["steering"]);
            Assert.Equal(600, SimpleJson.GetInt(evt.Data, "hold_ms"));
            Assert.Equal("TURN_LEFT", SimpleJson.GetString(evt.Data, "intent"));
            Assert.False(SimpleJson.GetBool(evt.Data, "refused"));
        }

        [Test]
        public static void Json_HandlesEscapesAndUnicode()
        {
            var payload = new Dictionary<string, object>
            {
                { "text", "路口\"左转\"\n第二行\t制表符" },
                { "emoji", "妹妹🙂" },
            };
            string written = SimpleJson.Write(payload);
            var roundtrip = SimpleJson.AsDict(SimpleJson.Parse(written));
            Assert.Equal("路口\"左转\"\n第二行\t制表符", roundtrip["text"]);
            Assert.Equal("妹妹🙂", roundtrip["emoji"]);
        }

        [Test]
        public static void Json_ParsesScalarsAndArrays()
        {
            var root = SimpleJson.AsDict(SimpleJson.Parse("{\"a\":[1,2.5,true,null,\"x\"],\"b\":-3}"));
            var list = (List<object>)root["a"];
            Assert.Equal(5, list.Count);
            Assert.Nearly(2.5, (double)list[1]);
            Assert.Equal(true, list[2]);
            Assert.True(list[3] == null);
            Assert.Nearly(-3, (double)root["b"]);
        }

        [Test]
        public static void Json_RejectsTrailingGarbage()
        {
            Assert.Throws<FormatException>(() => SimpleJson.Parse("{\"a\":1} trailing"));
            object value;
            Assert.False(SimpleJson.TryParse("{broken", out value));
        }

        // ---------- DriveAction / TurnEvent ----------

        [Test]
        public static void DriveAction_ParsesServerPayload()
        {
            var evt = TurnEvent.Parse(
                "{\"kind\":\"drive_action\",\"data\":{\"steering\":0.6,\"throttle\":0.4,\"brake\":0,\"hold_ms\":900,\"intent\":\"TURN_RIGHT\",\"confidence\":0.85,\"refused\":true,\"refuse_reason\":\"高速禁停\"}}");
            var action = evt.ToDriveAction();
            Assert.Nearly(0.6, action.Steering);
            Assert.Nearly(0.4, action.Throttle);
            Assert.Equal(900, action.HoldMs);
            Assert.Equal(Intents.TurnRight, action.Intent);
            Assert.True(action.Refused);
            Assert.Equal("高速禁停", action.RefuseReason);
        }

        [Test]
        public static void Intents_WhitelistMatchesPython()
        {
            Assert.True(Intents.IsAllowed("TURN_LEFT"));
            Assert.True(Intents.IsAllowed("QUESTION_BACK"));
            Assert.False(Intents.IsAllowed("RUN_RED_LIGHT"));
            Assert.False(Intents.IsAllowed(null));
            Assert.False(Intents.IsAllowed(""));
        }

        // ---------- ActionExecutorCore ----------

        [Test]
        public static void Executor_ClampsOutOfRangeValues()
        {
            var clamped = ActionExecutorCore.Clamp(new DriveAction
            {
                Steering = 1.5f,
                Throttle = -0.2f,
                Brake = 3f,
                HoldMs = 999999,
                Confidence = 4f,
            });
            Assert.Nearly(ActionExecutorCore.MaxSteer, clamped.Steering);
            Assert.Nearly(0.0, clamped.Throttle);
            Assert.Nearly(1.0, clamped.Brake);
            Assert.Equal(ActionExecutorCore.MaxHoldMs, clamped.HoldMs);
            Assert.Nearly(1.0, clamped.Confidence);
        }

        [Test]
        public static void Executor_SmoothStepsSteeringAndExpiresAfterHold()
        {
            var executor = new ActionExecutorCore();
            executor.Execute(new DriveAction { Steering = 0.8f, Throttle = 0.5f, HoldMs = 1000 });

            float steer, throttle, brake;
            executor.Tick(0f, out steer, out throttle, out brake);
            Assert.Nearly(0.0, steer, 1e-6, "smoothstep 从 0 起步");

            executor.Tick(500f, out steer, out throttle, out brake);
            Assert.Nearly(0.4, steer, 0.02, "中点约为指令一半");
            Assert.Nearly(0.25, throttle, 0.02);

            executor.Tick(400f, out steer, out throttle, out brake);
            Assert.True(steer > 0.7f, "接近指令值");

            executor.Tick(200f, out steer, out throttle, out brake);
            Assert.Nearly(0.0, steer, 1e-6, "hold 到期回中位");
            Assert.Nearly(0.0, throttle, 1e-6);
        }

        [Test]
        public static void Executor_ForciblyIgnoresUnknownIntent()
        {
            var executor = new ActionExecutorCore();
            var action = executor.Execute(new DriveAction { Intent = "DRUNK_DRIVING", Throttle = 1f });
            Assert.Equal(Intents.Ignore, action.Intent);
            Assert.Nearly(0.0, action.Throttle);
            Assert.True(action.Brake > 0f, "未知 intent 应刹停");
            Assert.True(executor.UnknownIntentSeen);

            float steer, throttle, brake;
            executor.Tick(100f, out steer, out throttle, out brake);
            Assert.Nearly(0.0, throttle, 1e-6);
        }

        [Test]
        public static void Executor_ZeroHoldMeansNoMotion()
        {
            var executor = new ActionExecutorCore();
            executor.Execute(new DriveAction { Steering = 0.5f, HoldMs = 0 });
            float steer, throttle, brake;
            executor.Tick(16f, out steer, out throttle, out brake);
            Assert.Nearly(0.0, steer);
            Assert.Nearly(0.0, throttle);
        }

        // ---------- StateView ----------

        [Test]
        public static void StateView_LocalEventTableMatchesPython()
        {
            var expected = new Dictionary<string, float>
            {
                { "collision", -12f }, { "red_light", -8f }, { "lane_cut", -4f },
                { "safe_arrival", 10f }, { "smooth_drive", 3f }, { "honk", -2f },
            };
            int count = 0;
            foreach (var kv in StateView.LocalEvents)
            {
                Assert.Equal(expected[kv.Key], kv.Value.TrustDelta, "本地事件 " + kv.Key);
                count++;
            }
            Assert.Equal(expected.Count, count);
        }

        [Test]
        public static void StateView_OfflineLocalEventClampsTrust()
        {
            var view = new StateView { Trust = 5f };
            Assert.True(view.ApplyLocalEventOffline("collision"));
            Assert.Nearly(0.0, view.Trust, 1e-6, "信任不会为负");
            Assert.False(view.ApplyLocalEventOffline("explode"));

            view.Trust = 95f;
            view.ApplyLocalEventOffline("safe_arrival");
            Assert.Nearly(100.0, view.Trust, 1e-6, "信任不超过 100");
        }

        [Test]
        public static void StateView_EmotionRulesMatchServer()
        {
            Assert.Equal(Emotion.Nervous, StateView.NextEmotion(15f, -1f, false, ""));
            Assert.Equal(Emotion.Annoyed, StateView.NextEmotion(50f, -5f, false, ""));
            Assert.Equal(Emotion.Annoyed, StateView.NextEmotion(35f, 3f, false, ""));
            Assert.Equal(Emotion.Happy, StateView.NextEmotion(85f, 3f, false, ""));
            // 与服务端一致：信任 ≥80 且 delta≥0 时优先 HAPPY（红灯分支在其后）
            Assert.Equal(Emotion.Happy, StateView.NextEmotion(85f, 3f, true, "前方红灯"));
            Assert.Equal(Emotion.Nervous, StateView.NextEmotion(60f, 0f, true, "前方红灯"));
            Assert.Equal(Emotion.Calm, StateView.NextEmotion(60f, 0f, true, "左侧有车"));
        }

        [Test]
        public static void StateView_SyncPayloadAndSnapshotRoundtrip()
        {
            var view = new StateView { Trust = 42f, Emotion = Emotion.Annoyed, SpeedKmh = 63.5f, Gear = 3 };
            var payload = view.ToSyncPayload();
            Assert.Nearly(42.0, SimpleJson.GetDouble(payload, "trust"));
            Assert.Equal("ANNOYED", SimpleJson.GetString(payload, "emotion"));
            var telemetry = SimpleJson.AsDict(payload["telemetry"]);
            Assert.Nearly(63.5, SimpleJson.GetDouble(telemetry, "speed_kmh"));
            Assert.Equal(3, SimpleJson.GetInt(telemetry, "gear"));

            var restored = new StateView();
            restored.LoadSnapshot(SimpleJson.AsDict(SimpleJson.Parse(SimpleJson.Write(payload))));
            Assert.Nearly(42.0, restored.Trust);
            Assert.Equal(Emotion.Annoyed, restored.Emotion);
            Assert.Nearly(63.5, restored.SpeedKmh);
        }

        [Test]
        public static void StateView_ServerDeltaDrivesEvent()
        {
            var view = new StateView();
            float seenTrust = -1f;
            Emotion seenEmotion = Emotion.Calm;
            view.StateChanged += (trust, emotion, delta) => { seenTrust = trust; seenEmotion = emotion; };
            view.ApplyServerDelta(55f, Emotion.Happy, 4f);
            Assert.Nearly(55.0, seenTrust);
            Assert.Equal(Emotion.Happy, seenEmotion);
            Assert.Nearly(4.0, view.LastDelta);
        }

        // ---------- PcmAudio ----------

        [Test]
        public static void Pcm_DecodesBase64WrapperFromServer()
        {
            byte[] raw = PcmAudio.ToPcm16(new float[] { 0f, 0.5f, -0.5f, 1f });
            string b64 = Convert.ToBase64String(raw);
            var evt = new TurnEvent("tts_audio", new Dictionary<string, object>
            {
                { "pcm16", new Dictionary<string, object> { { "__b64__", b64 } } },
                { "from_voicelib", true },
                { "text", "好的哥" },
            });
            var chunk = PcmAudio.FromEvent(evt);
            Assert.True(chunk != null);
            Assert.Equal(4, chunk.Samples.Length);
            Assert.Nearly(0.5, chunk.Samples[1], 0.01);
            Assert.True(chunk.FromVoiceLib);
            Assert.Equal("好的哥", chunk.Text);
        }

        [Test]
        public static void Pcm_ReturnsNullWhenMissingAudio()
        {
            Assert.True(PcmAudio.FromEvent(new TurnEvent("tts_audio", new Dictionary<string, object>())) == null);
            Assert.True(PcmAudio.FromEvent(null) == null);
            Assert.True(PcmAudio.DecodeBytes(new Dictionary<string, object> { { "__b64__", "!!!not-b64!!!" } }) == null);
        }

        [Test]
        public static void Pcm_Downsamples48kTo16k()
        {
            var input = new float[4800];
            for (int i = 0; i < input.Length; i++) input[i] = (float)Math.Sin(i * 0.05);
            var output = PcmAudio.Downsample(input, 48000, 16000);
            Assert.Equal(1600, output.Length);
            Assert.Equal(4800, PcmAudio.Downsample(input, 48000, 48000).Length, "同采样率直接透传");
        }
    }
}
