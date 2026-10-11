using System;
using System.Text;
using Copilot.AI;
using Copilot.Audio;
using Copilot.State;
using Copilot.UI;
using Copilot.Vehicle;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Copilot.EditorTools
{
    /// <summary>
    /// 生成「开箱可玩」的灰盒场景（P8/P9 之间的那一步）。
    ///
    /// 为什么要生成而不是手搭：手搭一次要挂十来个组件、连二十来处引用，还容易漏；
    /// 生成器是可重跑的**代码**，改一处结构重新生成即可，也便于以后接真实车模时替换几何体。
    ///
    /// 产物：`Assets/Scenes/Greybox.unity`
    /// - `Road`：长条路面（Cube 缩放，供 WheelCollider 抓地）
    /// - `Car`：Rigidbody + 4×WheelCollider + `VehicleController`（`VehiclePhysicsProfile` 资产）
    /// - `Main Camera`：`ChaseCamera` 跟随
    /// - `CopilotRig`：`CopilotConnection` + 执行/状态/音频/UI 全套 + `CopilotBootstrap`
    /// - `HUD`：信任条 / 情绪 / 字幕 / 飘条 / 录音指示（UGUI，无需 TextMeshPro）
    ///
    /// 用法：
    /// ① 编辑器菜单：`副驾指令 / 生成灰盒场景`
    /// ② 批处理：`-executeMethod Copilot.EditorTools.SceneBuilder.BuildAndExit`
    /// ③ 结构自检：`-executeMethod Copilot.EditorTools.SceneBuilder.ValidateAndExit`
    /// </summary>
    public static class SceneBuilder
    {
        const string SceneDir = "Assets/Scenes";
        const string ScenePath = SceneDir + "/Greybox.unity";
        const string ProfilePath = "Assets/VehiclePhysicsProfile.asset";

        const string RigName = "CopilotRig";
        const string CarName = "Car";

        [MenuItem("副驾指令/生成灰盒场景")]
        public static void BuildFromMenu()
        {
            Build();
            AssetDatabase.SaveAssets();
            EditorSceneManager.OpenScene(ScenePath);
            Debug.Log("[SceneBuilder] 已生成并打开 " + ScenePath);
        }

        [MenuItem("副驾指令/校验灰盒场景")]
        public static void ValidateFromMenu()
        {
            Validate();
        }

        public static void BuildAndExit() => RunAndExit(Build);
        public static void ValidateAndExit() => RunAndExit(Validate);

        static void RunAndExit(Action action)
        {
            int code;
            try
            {
                action();
                code = 0;
            }
            catch (Exception ex)
            {
                Debug.LogError("[SceneBuilder] 失败：" + ex);
                code = 1;
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        // ---------------------------------------------------------------- 生成

        public static void Build()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            CreateLight();

            var profile = EnsureProfile();
            var road = CreateRoad();
            var car = CreateCar(profile);
            CreateCamera(car.transform);
            var rig = CreateRig(car);
            CreateHud(rig);

            if (!AssetDatabase.IsValidFolder(SceneDir))
            {
                AssetDatabase.CreateFolder("Assets", "Scenes");
            }
            bool saved = EditorSceneManager.SaveScene(scene, ScenePath);
            if (!saved) throw new InvalidOperationException("场景保存失败：" + ScenePath);

            // 写进 Build Settings，免得以后打包时「一个场景都没有」
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };

            Debug.Log("[SceneBuilder] 场景已生成：" + ScenePath);
        }

        static VehiclePhysicsProfile EnsureProfile()
        {
            var existing = AssetDatabase.LoadAssetAtPath<VehiclePhysicsProfile>(ProfilePath);
            if (existing != null) return existing;

            var profile = ScriptableObject.CreateInstance<VehiclePhysicsProfile>();
            AssetDatabase.CreateAsset(profile, ProfilePath);
            return profile;
        }

        static void CreateLight()
        {
            var go = new GameObject("Directional Light");
            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            go.transform.rotation = Quaternion.Euler(48f, -35f, 0f);
        }

        static GameObject CreateRoad()
        {
            var road = GameObject.CreatePrimitive(PrimitiveType.Cube);
            road.name = "Road";
            road.transform.position = new Vector3(0f, -0.5f, 60f);
            road.transform.localScale = new Vector3(16f, 1f, 400f);
            road.GetComponent<Renderer>().sharedMaterial = MakeMaterial(new Color(0.22f, 0.23f, 0.25f));

            // 路肩：给点视觉参照，方便判断车在动
            for (int i = -1; i <= 1; i += 2)
            {
                var edge = GameObject.CreatePrimitive(PrimitiveType.Cube);
                edge.name = "Edge" + (i < 0 ? "L" : "R");
                edge.transform.SetParent(road.transform, false);
                edge.transform.localPosition = new Vector3(0.47f * i, 0.55f, 0f);
                edge.transform.localScale = new Vector3(0.06f, 0.1f, 1f);
                edge.GetComponent<Renderer>().sharedMaterial = MakeMaterial(new Color(0.8f, 0.8f, 0.78f));
            }
            return road;
        }

        static Material MakeMaterial(Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var mat = new Material(shader);
            mat.color = color;
            return mat;
        }

        static GameObject CreateCar(VehiclePhysicsProfile profile)
        {
            var car = new GameObject(CarName);
            car.transform.position = new Vector3(0f, 0.6f, 0f);

            var body = car.AddComponent<Rigidbody>();
            body.mass = 1400f;
            body.drag = 0.02f;          // 2022.3 是 drag/angularDrag（Unity 6 才改名 linearDamping）
            body.angularDrag = 0.4f;

            var chassis = GameObject.CreatePrimitive(PrimitiveType.Cube);
            chassis.name = "Body";
            chassis.transform.SetParent(car.transform, false);
            chassis.transform.localPosition = new Vector3(0f, 0.35f, 0f);
            chassis.transform.localScale = new Vector3(1.8f, 0.6f, 4f);
            chassis.GetComponent<Renderer>().sharedMaterial = MakeMaterial(new Color(0.75f, 0.2f, 0.18f));

            var controller = car.AddComponent<VehicleController>();
            controller.profile = profile;

            const float halfTrack = 0.85f;
            const float radius = 0.35f;
            controller.wheelFrontLeft = AddWheel(car.transform, "WheelFL", new Vector3(-halfTrack, radius, 1.35f), radius, out var meshFL);
            controller.wheelFrontRight = AddWheel(car.transform, "WheelFR", new Vector3(halfTrack, radius, 1.35f), radius, out var meshFR);
            controller.wheelRearLeft = AddWheel(car.transform, "WheelRL", new Vector3(-halfTrack, radius, -1.35f), radius, out var meshRL);
            controller.wheelRearRight = AddWheel(car.transform, "WheelRR", new Vector3(halfTrack, radius, -1.35f), radius, out var meshRR);
            controller.meshFrontLeft = meshFL;
            controller.meshFrontRight = meshFR;
            controller.meshRearLeft = meshRL;
            controller.meshRearRight = meshRR;

            return car;
        }

        static WheelCollider AddWheel(Transform parent, string name, Vector3 localPos, float radius, out Transform mesh)
        {
            var wheel = new GameObject(name);
            wheel.transform.SetParent(parent, false);
            wheel.transform.localPosition = localPos;

            var collider = wheel.AddComponent<WheelCollider>();
            collider.radius = radius;
            collider.suspensionDistance = 0.22f;
            var spring = collider.suspensionSpring;
            spring.spring = 35000f;
            spring.damper = 3500f;
            spring.targetPosition = 0.5f;
            collider.suspensionSpring = spring;

            // 抓地力：默认刚度偏低，灰盒跑起来会打滑
            var forward = collider.forwardFriction;
            forward.stiffness = 1.6f;
            collider.forwardFriction = forward;
            var sideways = collider.sidewaysFriction;
            sideways.stiffness = 1.6f;
            collider.sidewaysFriction = sideways;

            var visual = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            visual.name = "Mesh";
            visual.transform.SetParent(wheel.transform, false);
            visual.transform.localPosition = Vector3.zero;
            // Unity 的 Cylinder 沿 Y 轴、高 2 单位；转 90° 让轴朝 X，再按半径/胎宽缩放
            visual.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            visual.transform.localScale = new Vector3(radius * 2f, 0.12f, radius * 2f);
            visual.GetComponent<Renderer>().sharedMaterial = MakeMaterial(new Color(0.12f, 0.12f, 0.13f));
            UnityEngine.Object.DestroyImmediate(visual.GetComponent<Collider>()); // 轮子只靠 WheelCollider 物理

            mesh = visual.transform;
            return collider;
        }

        static void CreateCamera(Transform target)
        {
            // 新场景是空的，没有自带相机
            var go = new GameObject("Main Camera");
            go.tag = "MainCamera";
            var cam = go.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.53f, 0.68f, 0.82f);
            cam.farClipPlane = 800f;
            go.AddComponent<AudioListener>();

            var chase = go.AddComponent<ChaseCamera>();
            chase.target = target;
            go.transform.position = target.TransformPoint(chase.offset);
        }

        static GameObject CreateRig(GameObject car)
        {
            var rig = new GameObject(RigName);

            var connection = rig.AddComponent<CopilotConnection>();
            var vehicle = car.GetComponent<VehicleController>();

            var gameState = rig.AddComponent<GameStateManager>();
            gameState.connection = connection;
            gameState.vehicle = vehicle;

            var executor = rig.AddComponent<ActionExecutor>();
            executor.connection = connection;
            executor.vehicle = vehicle;

            var tts = rig.AddComponent<TtsPlayer>();
            tts.connection = connection;

            var mic = rig.AddComponent<MicCapture>();
            mic.connection = connection;
            mic.ttsPlayer = tts;

            var hud = rig.AddComponent<HudStatus>();
            hud.connection = connection;
            hud.gameState = gameState;
            hud.mic = mic;
            hud.ttsPlayer = tts;

            var bootstrap = rig.AddComponent<CopilotBootstrap>();
            bootstrap.vehicle = vehicle;
            bootstrap.connection = connection;
            bootstrap.executor = executor;
            bootstrap.gameState = gameState;
            bootstrap.ttsPlayer = tts;
            bootstrap.mic = mic;
            bootstrap.hud = hud;

            return rig;
        }

        static void CreateHud(GameObject rig)
        {
            var canvasGo = new GameObject("HUD", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            var root = canvasGo.transform;

            // 信任条（底 + 填充）
            var barBg = NewImage("TrustBarBg", root, new Color(0f, 0f, 0f, 0.45f));
            Anchor(barBg.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(30f, -70f), new Vector2(430f, -40f));

            var barFill = NewImage("TrustBar", root, new Color(0.36f, 0.85f, 0.48f, 1f));
            Anchor(barFill.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(30f, -70f), new Vector2(430f, -40f));
            barFill.type = Image.Type.Filled;
            barFill.fillMethod = Image.FillMethod.Horizontal;
            barFill.fillAmount = 0.6f;

            var trustLabel = NewText("TrustLabel", root, "60/100", 22, TextAnchor.MiddleLeft,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(444f, -70f), new Vector2(600f, -40f), Color.white);

            var emotionLabel = NewText("EmotionLabel", root, "CALM", 22, TextAnchor.MiddleRight,
                new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-330f, -70f), new Vector2(-30f, -40f), Color.white);

            var recordDot = NewImage("RecordDot", root, new Color(1f, 0.35f, 0.3f, 1f));
            Anchor(recordDot.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-60f, -60f), new Vector2(-30f, -30f));
            recordDot.enabled = false;

            // 字幕
            var subtitle = NewText("Subtitle", root, "", 34, TextAnchor.LowerCenter,
                new Vector2(0.1f, 0f), new Vector2(0.9f, 0f), new Vector2(0f, 120f), new Vector2(0f, 240f), Color.white);
            subtitle.horizontalOverflow = HorizontalWrapMode.Wrap;

            // 飘条
            var toast = NewText("Toast", root, "", 24, TextAnchor.UpperCenter,
                new Vector2(0.15f, 1f), new Vector2(0.85f, 1f), new Vector2(0f, -150f), new Vector2(0f, -110f),
                new Color(1f, 0.9f, 0.55f, 1f));

            NewText("Hint", root, "按住空格说话 · 文本调试见 CopilotBootstrap.SendInstruction", 20,
                TextAnchor.LowerLeft, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(30f, 30f), new Vector2(900f, 60f), new Color(1f, 1f, 1f, 0.6f));

            var bubble = rig.AddComponent<DialogueBubble>();
            bubble.connection = rig.GetComponent<CopilotConnection>();
            bubble.label = subtitle;

            // 装配方（CopilotBootstrap）也要拿到 bubble —— 漏掉这条它就不会 Rebind
            var bootstrap = rig.GetComponent<CopilotBootstrap>();
            if (bootstrap != null) bootstrap.bubble = bubble;

            var hud = rig.GetComponent<HudStatus>();
            hud.trustBar = barFill;
            hud.trustLabel = trustLabel;
            hud.emotionLabel = emotionLabel;
            hud.toastLabel = toast;
            hud.recordIndicator = recordDot;
        }

        static Font BuiltinFont()
        {
            // 2022.3 起内置字体改名；两个都试，保证新老版本都能生成
            return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                   ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
        }

        static Image NewImage(string name, Transform parent, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            // 显式给内置 sprite：Image 没有 sprite 时不一定渲染出可见矩形
            image.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
            image.type = Image.Type.Sliced;
            image.color = color;
            return image;
        }

        static Text NewText(string name, Transform parent, string content, int fontSize, TextAnchor anchor,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<Text>();
            text.font = BuiltinFont();
            text.fontSize = fontSize;
            text.text = content;
            text.alignment = anchor;
            text.color = color;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            Anchor(text.rectTransform, anchorMin, anchorMax, offsetMin, offsetMax);
            return text;
        }

        static void Anchor(RectTransform rt, Vector2 min, Vector2 max, Vector2 offsetMin, Vector2 offsetMax)
        {
            rt.anchorMin = min;
            rt.anchorMax = max;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
        }

        // ---------------------------------------------------------------- 校验

        /// <summary>
        /// 结构自检：打开生成的场景，确认该连的地方都连上了。
        /// 生成器最容易出的错就是「漏连一个引用」——运行前不报错，Play 了才发现没反应。
        /// </summary>
        public static void Validate()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var problems = new StringBuilder();

            var bootstrap = FindInScene<CopilotBootstrap>(scene);
            Requires(bootstrap != null, "场景里找不到 CopilotBootstrap", problems);
            if (bootstrap == null) throw Fail(problems);

            Requires(bootstrap.vehicle != null, "CopilotBootstrap.vehicle 未连", problems);
            Requires(bootstrap.connection != null, "CopilotBootstrap.connection 未连", problems);
            Requires(bootstrap.executor != null, "CopilotBootstrap.executor 未连", problems);
            Requires(bootstrap.gameState != null, "CopilotBootstrap.gameState 未连", problems);
            Requires(bootstrap.ttsPlayer != null, "CopilotBootstrap.ttsPlayer 未连", problems);
            Requires(bootstrap.mic != null, "CopilotBootstrap.mic 未连", problems);
            Requires(bootstrap.hud != null, "CopilotBootstrap.hud 未连", problems);

            var vehicle = bootstrap.vehicle;
            if (vehicle != null)
            {
                Requires(vehicle.profile != null, "VehicleController.profile 未连", problems);
                Requires(vehicle.wheelFrontLeft != null && vehicle.wheelFrontRight != null
                         && vehicle.wheelRearLeft != null && vehicle.wheelRearRight != null,
                    "四个 WheelCollider 未连全", problems);
                Requires(vehicle.meshFrontLeft != null && vehicle.meshRearRight != null,
                    "车轮可视网格未连", problems);
            }

            var hud = bootstrap.hud;
            if (hud != null)
            {
                Requires(hud.trustBar != null, "HudStatus.trustBar 未连", problems);
                Requires(hud.trustLabel != null, "HudStatus.trustLabel 未连", problems);
                Requires(hud.emotionLabel != null, "HudStatus.emotionLabel 未连", problems);
                Requires(hud.toastLabel != null, "HudStatus.toastLabel 未连", problems);
                Requires(hud.recordIndicator != null, "HudStatus.recordIndicator 未连", problems);
            }

            var bubble = FindInScene<DialogueBubble>(scene);
            Requires(bubble != null && bubble.label != null, "DialogueBubble.label 未连", problems);
            Requires(bubble != null && bubble.connection != null, "DialogueBubble.connection 未连", problems);

            var chase = FindInScene<ChaseCamera>(scene);
            Requires(chase != null && chase.target != null, "ChaseCamera.target 未连", problems);
            Requires(FindInScene<Camera>(scene) != null, "场景里没有 Camera", problems);

            if (problems.Length > 0) throw Fail(problems);

            Debug.Log("[SceneBuilder] 校验通过：场景结构完整（" + scene.path + "）");
        }

        static T FindInScene<T>(Scene scene) where T : Component
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                var found = root.GetComponentInChildren<T>(true);
                if (found != null) return found;
            }
            return null;
        }

        static void Requires(bool condition, string message, StringBuilder problems)
        {
            if (condition) return;
            problems.Append("\n  ✗ ").Append(message);
        }

        static Exception Fail(StringBuilder problems)
        {
            return new InvalidOperationException("场景校验未通过：" + problems);
        }
    }
}