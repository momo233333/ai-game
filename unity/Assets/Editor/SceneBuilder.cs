using System;
using System.IO;
using System.Text;
using Copilot.AI;
using Copilot.Audio;
using Copilot.Gameplay;
using Copilot.State;
using Copilot.UI;
using Copilot.Vehicle;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Copilot.EditorTools
{
    /// <summary>
    /// 生成「开箱可玩」的场景（P8 灰盒 → P9 街道）。
    ///
    /// 为什么用代码生成而不是手搭：手搭一次要挂十来个组件、连二十来处引用，还容易漏；
    /// 生成器是可重跑的**代码**，改一处结构重新生成即可。美术资产到位后也只改
    /// <c>CreateVehicle</c>/<c>CreateCity</c>，装配与 UI 逻辑不用动。
    ///
    /// 产物：`Assets/Scenes/Greybox.unity`
    /// - `City`：路面 + 车道线 + 人行道 + 路口 + 楼群 + 路灯 + 锥桶 + 路边停车
    /// - `Car`：Rigidbody + 4×WheelCollider + `VehicleController`（`VehiclePhysicsProfile` 资产）
    /// - `Main Camera`：`ChaseCamera` 跟随
    /// - `CopilotRig`：`CopilotConnection` + 执行/状态/音频/UI 全套 + `CopilotBootstrap`
    /// - `HUD`：信任条 / 情绪 / 字幕 / 飘条 / 录音指示（UGUI，无需 TextMeshPro）
    ///
    /// 用法（编辑器菜单或 batchmode）：
    /// - `副驾指令 / 生成灰盒场景`      → `SceneBuilder.BuildAndExit`
    /// - `副驾指令 / 校验灰盒场景`      → `SceneBuilder.ValidateAndExit`（断言引用都连上了）
    /// - `副驾指令 / 渲染场景预览图`    → `SceneBuilder.CaptureAndExit`（渲一张 PNG，供人/CI 看画面）
    /// </summary>
    public static class SceneBuilder
    {
        const string SceneDir = "Assets/Scenes";
        const string ScenePath = SceneDir + "/Greybox.unity";
        const string ProfilePath = "Assets/VehiclePhysicsProfile.asset";
        const string PreviewRelativePath = "logs/unity_scene_preview.png";
        const string DriverPreviewRelativePath = "logs/unity_scene_preview_driver.png";

        const string RigName = "CopilotRig";
        const string CarName = "Car";

        /// <summary>固定随机种子：每次生成的楼群/道具布局完全一致，便于对比与回归。</summary>
        const int Seed = 20261011;

        // 路面参数（车辆与道具都按这些摆放，改这里就整体改）
        const float RoadHalfWidth = 8f;
        const float RoadLength = 360f;
        const float RoadCenterZ = 120f;
        const float LaneMarkZ = 0.5f;
        const float CrossZ = 120f;
        const float CrossHalfWidth = 8f;

        // 共用材质（一次构建内复用，避免几百个材质实例）
        static Material _road, _laneSolid, _laneDash, _sidewalk, _curb;
        static Material _bldA, _bldB, _bldC, _window, _roof;
        static Material _pole, _lamp;
        static Material _body, _glass, _tire, _rim, _lightFront, _lightRear, _cone;
        static Material _dash, _console, _seat, _cabinTrim, _skin, _hair, _cloth, _mirror;

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

        [MenuItem("副驾指令/渲染场景预览图")]
        public static void CaptureFromMenu()
        {
            var png = Capture();
            Debug.Log("[SceneBuilder] 预览图：" + png);
        }

        public static void BuildAndExit() => RunAndExit(Build);
        public static void ValidateAndExit() => RunAndExit(Validate);
        public static void CaptureAndExit() => RunAndExit(() => Capture());

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

            CreatePalette();
            SetupEnvironment();
            CreateLight();

            var profile = EnsureProfile();
            var city = CreateCity();
            var car = CreateVehicle(profile, city.transform);   // 相机随车一起建（第一人称副驾座）
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

        static void CreatePalette()
        {
            _road = MakeMaterial(new Color(0.20f, 0.21f, 0.23f));
            _laneSolid = MakeMaterial(new Color(0.88f, 0.88f, 0.85f));
            _laneDash = MakeMaterial(new Color(0.92f, 0.92f, 0.88f));
            _sidewalk = MakeMaterial(new Color(0.55f, 0.55f, 0.54f));
            _curb = MakeMaterial(new Color(0.72f, 0.72f, 0.70f));

            _bldA = MakeMaterial(new Color(0.62f, 0.60f, 0.58f));
            _bldB = MakeMaterial(new Color(0.48f, 0.50f, 0.54f));
            _bldC = MakeMaterial(new Color(0.70f, 0.64f, 0.56f));
            _window = MakeMaterial(new Color(0.10f, 0.12f, 0.16f));
            _roof = MakeMaterial(new Color(0.30f, 0.31f, 0.33f));

            _pole = MakeMaterial(new Color(0.35f, 0.36f, 0.38f));
            _lamp = MakeMaterial(new Color(1f, 0.95f, 0.75f), new Color(1f, 0.9f, 0.6f) * 2f);

            _body = MakeMaterial(new Color(0.72f, 0.18f, 0.16f));
            _glass = MakeMaterial(new Color(0.12f, 0.16f, 0.20f));
            _tire = MakeMaterial(new Color(0.09f, 0.09f, 0.10f));
            _rim = MakeMaterial(new Color(0.75f, 0.76f, 0.78f));
            _lightFront = MakeMaterial(new Color(1f, 0.98f, 0.85f), new Color(1f, 0.97f, 0.8f) * 1.5f);
            _lightRear = MakeMaterial(new Color(0.9f, 0.15f, 0.12f), new Color(1f, 0.15f, 0.1f) * 1.5f);
            _cone = MakeMaterial(new Color(0.95f, 0.45f, 0.10f));

            // 车内（第一人称副驾视角要看到的东西）
            _dash = MakeMaterial(new Color(0.16f, 0.16f, 0.17f));
            _console = MakeMaterial(new Color(0.11f, 0.11f, 0.12f));
            _seat = MakeMaterial(new Color(0.20f, 0.19f, 0.20f));
            _cabinTrim = MakeMaterial(new Color(0.34f, 0.34f, 0.35f));
            _mirror = MakeMaterial(new Color(0.10f, 0.11f, 0.13f));
            _skin = MakeMaterial(new Color(0.93f, 0.80f, 0.70f));
            _hair = MakeMaterial(new Color(0.15f, 0.11f, 0.10f));
            _cloth = MakeMaterial(new Color(0.42f, 0.52f, 0.68f));   // 学员的衣服
        }

        static void SetupEnvironment()
        {
            // 雾：城市远景不至于「一刀切」地消失在裁剪面
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = 0.0035f;
            RenderSettings.fogColor = new Color(0.62f, 0.72f, 0.82f);

            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.42f, 0.44f, 0.48f);
        }

        static void CreateLight()
        {
            var go = new GameObject("Directional Light");
            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.15f;
            light.shadows = LightShadows.Soft;
            go.transform.rotation = Quaternion.Euler(48f, -35f, 0f);
        }

        static VehiclePhysicsProfile EnsureProfile()
        {
            var existing = AssetDatabase.LoadAssetAtPath<VehiclePhysicsProfile>(ProfilePath);
            if (existing != null) return existing;

            var profile = ScriptableObject.CreateInstance<VehiclePhysicsProfile>();
            AssetDatabase.CreateAsset(profile, ProfilePath);
            return profile;
        }

        // ---- 场景几何 -------------------------------------------------

        static GameObject CreateCity()
        {
            var city = new GameObject("City");
            var rng = new System.Random(Seed);

            CreateRoad(city.transform);
            CreateBuildings(city.transform, rng);
            CreateStreetLights(city.transform);
            CreateProps(city.transform, rng);
            return city;
        }

        static void CreateRoad(Transform parent)
        {
            Box("Road", parent, new Vector3(0f, -0.5f, RoadCenterZ), new Vector3(RoadHalfWidth * 2f, 1f, RoadLength), _road);
            // 横向路口（让「左转/右转」在场景里真的有意义）
            Box("CrossRoad", parent, new Vector3(0f, -0.5f, CrossZ), new Vector3(160f, 1f, CrossHalfWidth * 2f), _road);

            // 人行道 + 路缘
            for (int side = -1; side <= 1; side += 2)
            {
                float x = side * (RoadHalfWidth + 1.8f);
                Box("Sidewalk", parent, new Vector3(x, 0.15f, RoadCenterZ), new Vector3(3.6f, 0.3f, RoadLength), _sidewalk);
                Box("Curb", parent, new Vector3(side * (RoadHalfWidth + 0.12f), 0.16f, RoadCenterZ), new Vector3(0.24f, 0.34f, RoadLength), _curb);
            }

            // 车道线：中心实线 + 两侧实线 + 车道分隔虚线（路口区间断）
            // 注意 y 必须**高于**路面顶面（路面顶面在 y=0），否则整条线埋进沥青里看不见 —— 踩过
            const float markY = 0.012f;
            const float markThickness = 0.02f;

            Box("CenterLine", parent, new Vector3(0f, markY, RoadCenterZ), new Vector3(0.16f, markThickness, RoadLength), _laneSolid);
            for (int side = -1; side <= 1; side += 2)
            {
                Box("LaneEdge", parent, new Vector3(side * 7.2f, markY, RoadCenterZ), new Vector3(0.18f, markThickness, RoadLength), _laneSolid);
            }

            for (int side = -1; side <= 1; side += 2)
            {
                for (float z = -RoadLength / 2f; z < RoadLength / 2f; z += 8f)
                {
                    float world = RoadCenterZ + z;
                    if (Mathf.Abs(world - CrossZ) < CrossHalfWidth + 3f) continue; // 路口不画线
                    Box("LaneDash", parent, new Vector3(side * 3.6f, markY, world), new Vector3(0.16f, markThickness, 3.4f), _laneDash);
                }
            }

            // 斑马线（路口四个方向）
            for (int dir = -1; dir <= 1; dir += 2)
            {
                for (int i = -3; i <= 3; i++)
                {
                    Box("Zebra", parent,
                        new Vector3(i * 1.35f, markY, CrossZ + dir * (CrossHalfWidth + 1.6f)),
                        new Vector3(0.8f, markThickness, 1.8f), _laneSolid);
                }
            }
        }

        static void CreateBuildings(Transform parent, System.Random rng)
        {
            var facades = new[] { _bldA, _bldB, _bldC };

            for (int side = -1; side <= 1; side += 2)
            {
                for (float z = -40f; z <= RoadLength - 40f; z += 26f)
                {
                    if (Mathf.Abs(RoadCenterZ + z - CrossZ) < CrossHalfWidth + 6f) continue; // 路口留空

                    float width = 14f + (float)rng.NextDouble() * 8f;      // 沿 Z 的进深
                    float depth = 12f + (float)rng.NextDouble() * 8f;      // 沿 X 的宽度
                    float height = 12f + (float)rng.NextDouble() * 26f;
                    float x = side * (RoadHalfWidth + 4f + depth / 2f);

                    var facade = facades[rng.Next(facades.Length)];
                    var b = Box("Building", parent,
                        new Vector3(x, height / 2f, RoadCenterZ + z),
                        new Vector3(depth, height, width), facade);

                    // 屋顶设备箱：高楼才有，制造天际线高低差
                    if (height > 22f)
                    {
                        Box("RoofBox", b.transform, new Vector3(0f, 0.5f + 1.2f / height, 0f),
                            new Vector3(0.4f, 2.4f / height, 0.4f), _roof);
                    }

                    // 面向马路的窗带
                    float faceX = -side * (0.5f + 0.01f);
                    for (int band = 1; band <= 3; band++)
                    {
                        float t = band / 4f + 0.06f;
                        Box("WindowBand", b.transform,
                            new Vector3(faceX, t - 0.5f, 0f),
                            new Vector3(0.02f, 3.2f / height, 0.78f), _window);
                    }
                }
            }
        }

        static void CreateStreetLights(Transform parent)
        {
            for (int side = -1; side <= 1; side += 2)
            {
                for (float z = -20f; z <= RoadLength - 40f; z += 34f)
                {
                    float worldZ = RoadCenterZ + z;
                    if (Mathf.Abs(worldZ - CrossZ) < CrossHalfWidth + 4f) continue;

                    var pole = new GameObject("StreetLight");
                    pole.transform.SetParent(parent, false);
                    pole.transform.localPosition = new Vector3(side * (RoadHalfWidth + 1.2f), 0f, worldZ);

                    Cylinder("Pole", pole.transform, new Vector3(0f, 3.5f, 0f), new Vector3(0.22f, 3.5f, 0.22f), _pole);
                    Box("Arm", pole.transform, new Vector3(-side * 1.1f, 6.9f, 0f), new Vector3(2.2f, 0.16f, 0.16f), _pole);
                    Box("Lamp", pole.transform, new Vector3(-side * 2.1f, 6.82f, 0f), new Vector3(0.7f, 0.14f, 0.36f), _lamp);
                }
            }
        }

        static void CreateProps(Transform parent, System.Random rng)
        {
            // 路口锥桶
            for (int i = 0; i < 4; i++)
            {
                float z = CrossZ + CrossHalfWidth + 3f + i * 2.2f;
                Cylinder("Cone", parent, new Vector3(-5.4f, 0.35f, z), new Vector3(0.5f, 0.35f, 0.5f), _cone);
            }

            // 路边停车（用简化车身，不做车轮碰撞）
            for (int i = 0; i < 3; i++)
            {
                float z = RoadCenterZ - 70f + i * 34f;
                var parked = new GameObject("ParkedCar");
                parked.transform.SetParent(parent, false);
                parked.transform.localPosition = new Vector3(6f, 0f, z);
                BuildCarBody(parked.transform, new Color(0.25f + 0.3f * (float)rng.NextDouble(), 0.3f, 0.55f), withWheels: true);
            }
        }

        // ---- 车辆 -----------------------------------------------------

        static GameObject CreateVehicle(VehiclePhysicsProfile profile, Transform parent)
        {
            var car = new GameObject(CarName);
            car.transform.position = new Vector3(1.8f, 0.0f, 0f); // 靠右行驶：右内侧车道（x>0）

            var body = car.AddComponent<Rigidbody>();
            body.mass = 1400f;
            body.drag = 0.02f;          // 2022.3 是 drag/angularDrag（Unity 6 才改名 linearDamping）
            body.angularDrag = 0.4f;

            // 玩家的车要「敞篷」：不开顶盖的实心 Cabin，改由车内件（顶棚/A柱/门板）拼，
            // 否则第一人称相机就坐在实心盒子里，朝前看是一片黑。
            BuildCarBody(car.transform, new Color(0.72f, 0.18f, 0.16f), withWheels: true, openCabin: true);

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

            CreateInterior(car.transform, controller);
            CreateCockpitCamera(car.transform);
            return car;
        }

        // ---- 车内（第一人称副驾视角） ----------------------------------

        /// <summary>
        /// 车内件 + 驾驶位上的学员。坐标约定：车头朝 +Z、+X 是车的右侧
        /// —— 所以**驾驶位在 x&lt;0（左舵）**，副驾（相机）在 x&gt;0。
        /// 所有车内件都不带碰撞体：它们套在车体碰撞盒里，多一层只会徒增抖动。
        /// </summary>
        static void CreateInterior(Transform car, VehicleController controller)
        {
            // 顶棚 / 后隔板 / 两侧门板 / A 柱 / 前挡上沿 —— 拼出「有壳但不挡视线」的座舱
            Box("Roof", car, new Vector3(0f, 1.52f, -0.15f), new Vector3(1.68f, 0.07f, 2.1f), _cabinTrim, withCollider: false);
            Box("RearBulkhead", car, new Vector3(0f, 1.12f, -1.42f), new Vector3(1.66f, 0.8f, 0.08f), _cabinTrim, withCollider: false);
            for (int sx = -1; sx <= 1; sx += 2)
            {
                Box("DoorPanel", car, new Vector3(sx * 0.86f, 0.86f, -0.2f), new Vector3(0.06f, 0.62f, 2.0f), _cabinTrim, withCollider: false);
                Box("APillar", car, new Vector3(sx * 0.74f, 1.3f, 0.92f), new Vector3(0.1f, 0.56f, 0.1f), _cabinTrim, withCollider: false);
            }
            Box("Header", car, new Vector3(0f, 1.5f, 1.0f), new Vector3(1.62f, 0.1f, 0.1f), _cabinTrim, withCollider: false);

            // 车内地板：车身的顶面（红色）同时也是引擎盖，不加这块的话座位前方会是一片红
            Box("Floor", car, new Vector3(0f, 0.885f, -0.25f), new Vector3(1.66f, 0.02f, 2.1f), _dash, withCollider: false);

            // 仪表台 + 中控 + 转向柱
            // 仪表台顶面 0.98，比视线（相机 y=1.26）低 0.28 —— 刚好挡住近处的引擎盖，
            // 只留远处一小条；再低就露出红色顶面，再高就变成一堵墙（两个极端都试过了）。
            Box("Dash", car, new Vector3(0f, 0.86f, 0.95f), new Vector3(1.64f, 0.24f, 0.7f), _dash, withCollider: false);
            Box("Console", car, new Vector3(0f, 0.7f, 0.05f), new Vector3(0.34f, 0.34f, 1.4f), _console, withCollider: false);
            Box("SteeringColumn", car, new Vector3(-0.42f, 0.86f, 0.66f), new Vector3(0.09f, 0.09f, 0.34f), _console, withCollider: false);

            // 后视镜
            Box("MirrorStalk", car, new Vector3(0f, 1.42f, 0.86f), new Vector3(0.04f, 0.12f, 0.04f), _console, withCollider: false);
            Box("RearViewMirror", car, new Vector3(0f, 1.36f, 0.86f), new Vector3(0.34f, 0.1f, 0.04f), _mirror, withCollider: false);

            // 座椅（驾驶位 + 副驾）
            for (int sx = -1; sx <= 1; sx += 2)
            {
                float x = sx * 0.42f;
                Box("SeatBack", car, new Vector3(x, 1.02f, -0.62f), new Vector3(0.62f, 0.98f, 0.16f), _seat, withCollider: false);
                Box("SeatCushion", car, new Vector3(x, 0.72f, -0.22f), new Vector3(0.62f, 0.14f, 0.7f), _seat, withCollider: false);
            }

            // 方向盘（脚本驱动转动）+ 盘辐让转动看得出来
            var wheelRoot = new GameObject("SteeringWheel");
            wheelRoot.transform.SetParent(car, false);
            wheelRoot.transform.localPosition = new Vector3(-0.42f, 0.92f, 0.48f);

            Cylinder("Disc", wheelRoot.transform, Vector3.zero, new Vector3(0.36f, 0.035f, 0.36f), _console,
                Quaternion.Euler(90f, 0f, 0f), withCollider: false);
            Box("SpokeH", wheelRoot.transform, new Vector3(0f, 0f, 0.02f), new Vector3(0.34f, 0.035f, 0.03f), _cabinTrim, withCollider: false);
            Box("SpokeV", wheelRoot.transform, new Vector3(0f, 0f, 0.02f), new Vector3(0.035f, 0.34f, 0.03f), _cabinTrim, withCollider: false);

            var wheel = wheelRoot.AddComponent<SteeringWheel>();
            wheel.vehicle = controller;

            CreateDriverAvatar(car, wheel);
        }

        /// <summary>驾驶位上的学员小雅（基本体拼的人形）。说话时会扭头看教练，由 <see cref="CreateRig"/> 接上 ttsPlayer。</summary>
        static void CreateDriverAvatar(Transform car, SteeringWheel wheel)
        {
            var avatar = new GameObject("DriverAvatar");
            avatar.transform.SetParent(car, false);
            avatar.transform.localPosition = new Vector3(-0.42f, 0.68f, -0.2f);

            Box("Torso", avatar.transform, new Vector3(0f, 0.24f, 0.02f), new Vector3(0.38f, 0.52f, 0.26f), _cloth, withCollider: false);

            var head = new GameObject("Head").transform;
            head.SetParent(avatar.transform, false);
            head.localPosition = new Vector3(0f, 0.62f, 0.02f);
            Cylinder("Face", head, new Vector3(0f, 0f, 0.02f), new Vector3(0.24f, 0.12f, 0.24f), _skin, Quaternion.Euler(90f, 0f, 0f), withCollider: false);
            Cylinder("Hair", head, new Vector3(0f, 0.03f, -0.03f), new Vector3(0.25f, 0.1f, 0.25f), _hair, Quaternion.Euler(90f, 0f, 0f), withCollider: false);

            // 两只前臂伸向方向盘（DriverAvatar 让它们跟着盘转）
            var leftArm = new GameObject("ArmL").transform;
            leftArm.SetParent(avatar.transform, false);
            leftArm.localPosition = new Vector3(-0.1f, 0.3f, 0.3f);
            leftArm.localRotation = Quaternion.Euler(-18f, 0f, 0f);
            Box("ForearmL", leftArm, new Vector3(0f, 0f, 0.24f), new Vector3(0.1f, 0.1f, 0.5f), _cloth, withCollider: false);
            Box("HandL", leftArm, new Vector3(0f, 0f, 0.48f), new Vector3(0.11f, 0.11f, 0.12f), _skin, withCollider: false);

            var rightArm = new GameObject("ArmR").transform;
            rightArm.SetParent(avatar.transform, false);
            rightArm.localPosition = new Vector3(0.1f, 0.3f, 0.3f);
            rightArm.localRotation = Quaternion.Euler(-18f, 0f, 0f);
            Box("ForearmR", rightArm, new Vector3(0f, 0f, 0.24f), new Vector3(0.1f, 0.1f, 0.5f), _cloth, withCollider: false);
            Box("HandR", rightArm, new Vector3(0f, 0f, 0.48f), new Vector3(0.11f, 0.11f, 0.12f), _skin, withCollider: false);

            var driver = avatar.AddComponent<DriverAvatar>();
            driver.wheel = wheel;
            driver.head = head;
            driver.leftArm = leftArm;
            driver.rightArm = rightArm;
        }

        /// <summary>
        /// 用基本体拼一台「看得出是车」的车（供玩家车与路边停车共用）。
        /// 真车模到位后整个换掉这里即可，WheelCollider 的挂点由 <see cref="AddWheel"/> 负责，不受影响。
        /// </summary>
        /// <param name="openCabin">
        /// true = 不建实心驾驶舱与玻璃（玩家的车用：由 <see cref="CreateInterior"/> 拼出中空的座舱，
        /// 否则第一人称相机就在实心盒子里，朝前看是一片黑）；false = 路边停车那种整块外壳。
        /// </param>
        static void BuildCarBody(Transform root, Color color, bool withWheels, bool openCabin = false)
        {
            var bodyMat = MakeMaterial(color);

            // 底盘 + 机舱盖 + 后备箱
            Box("Chassis", root, new Vector3(0f, 0.62f, 0f), new Vector3(1.82f, 0.5f, 4.2f), bodyMat);
            Box("Hood", root, new Vector3(0f, 0.92f, 1.35f), new Vector3(1.72f, 0.22f, 1.4f), bodyMat);
            Box("Trunk", root, new Vector3(0f, 0.92f, -1.45f), new Vector3(1.72f, 0.22f, 1.1f), bodyMat);

            if (!openCabin)
            {
                // 驾驶舱（略窄、偏后）
                Box("Cabin", root, new Vector3(0f, 1.14f, -0.25f), new Vector3(1.6f, 0.56f, 2f), bodyMat);

                // 玻璃：前风挡 / 后风挡 / 两侧窗
                Box("Windshield", root, new Vector3(0f, 1.12f, 0.78f), new Vector3(1.5f, 0.5f, 0.06f), _glass);
                Box("RearGlass", root, new Vector3(0f, 1.12f, -1.28f), new Vector3(1.5f, 0.5f, 0.06f), _glass);
                Box("SideGlassL", root, new Vector3(-0.81f, 1.14f, -0.25f), new Vector3(0.04f, 0.42f, 1.7f), _glass);
                Box("SideGlassR", root, new Vector3(0.81f, 1.14f, -0.25f), new Vector3(0.04f, 0.42f, 1.7f), _glass);
            }

            // 灯
            Box("HeadL", root, new Vector3(-0.62f, 0.78f, 2.08f), new Vector3(0.42f, 0.18f, 0.08f), _lightFront);
            Box("HeadR", root, new Vector3(0.62f, 0.78f, 2.08f), new Vector3(0.42f, 0.18f, 0.08f), _lightFront);
            Box("TailL", root, new Vector3(-0.6f, 0.82f, -2.08f), new Vector3(0.4f, 0.16f, 0.08f), _lightRear);
            Box("TailR", root, new Vector3(0.6f, 0.82f, -2.08f), new Vector3(0.4f, 0.16f, 0.08f), _lightRear);

            if (!withWheels) return;

            // 静态车轮（路边停车用；玩家车的轮子由 WheelCollider 管）
            const float halfTrack = 0.85f;
            for (int sx = -1; sx <= 1; sx += 2)
            {
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    WheelVisual(root, new Vector3(sx * halfTrack, 0.35f, sz * 1.35f), 0.35f);
                }
            }
        }

        static void WheelVisual(Transform parent, Vector3 localPos, float radius)
        {
            var go = new GameObject("WheelVisual");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);

            Cylinder("Tire", go.transform, Vector3.zero, new Vector3(radius * 2f, 0.12f, radius * 2f), _tire);
            Cylinder("Rim", go.transform, Vector3.zero, new Vector3(radius * 1.1f, 0.13f, radius * 1.1f), _rim);
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

            // 可视轮挂在 WheelCollider 下，由 VehicleController 每帧按真实位姿同步
            var visual = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            visual.name = "Mesh";
            visual.transform.SetParent(wheel.transform, false);
            visual.transform.localPosition = Vector3.zero;
            // Unity 的 Cylinder 沿 Y 轴、高 2 单位；转 90° 让轴朝 X，再按半径/胎宽缩放
            visual.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            visual.transform.localScale = new Vector3(radius * 2f, 0.12f, radius * 2f);
            visual.GetComponent<Renderer>().sharedMaterial = _tire;
            UnityEngine.Object.DestroyImmediate(visual.GetComponent<Collider>()); // 轮子只靠 WheelCollider 物理

            mesh = visual.transform;
            return collider;
        }

        /// <summary>
        /// 第一人称副驾相机：**挂在 Car 下面**，localPosition 就是副驾座，随车身一起动。
        /// 近裁剪面调到 0.05，否则仪表台/方向盘会被裁掉一片。
        /// </summary>
        static void CreateCockpitCamera(Transform car)
        {
            var go = new GameObject("Main Camera");
            go.tag = "MainCamera";
            go.transform.SetParent(car, false);
            // 相机再往右后挪一点、并朝驾驶位偏 12°，这样正前方就能同时看到路和学员的侧脸
            go.transform.localPosition = new Vector3(0.52f, 1.26f, -0.42f);   // 副驾座（左舵车：驾驶位在 x<0）
            go.transform.localRotation = Quaternion.identity;

            var cam = go.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.62f, 0.72f, 0.82f);
            cam.farClipPlane = 800f;
            cam.nearClipPlane = 0.05f;
            cam.fieldOfView = 68f;      // 车内视角视野开一点，看得出车速感
            go.AddComponent<AudioListener>();

            var cockpit = go.AddComponent<CockpitCamera>();
            cockpit.allowLook = true;
            cockpit.restYaw = -12f;
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

            // 学员说话时扭头看教练 —— ttsPlayer 在 rig 上，所以只能建完 rig 再回填
            var avatar = car.GetComponentInChildren<DriverAvatar>(true);
            if (avatar != null) avatar.ttsPlayer = tts;

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

            NewText("Hint", root, "按住空格说话 · Q/E 或按住鼠标右键扭头看学员", 20,
                TextAnchor.LowerLeft, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(30f, 30f), new Vector2(900f, 60f), new Color(1f, 1f, 1f, 0.6f));

            // 教学关：当前步骤 + 结果播报（科目一 · 上车第一课）
            var stepLabel = NewText("StepLabel", root, "", 24, TextAnchor.UpperLeft,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(30f, -232f), new Vector2(900f, -108f),
                new Color(0.95f, 0.97f, 1f, 0.92f));

            var resultLabel = NewText("ResultLabel", root, "", 22, TextAnchor.UpperLeft,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(30f, -272f), new Vector2(900f, -236f),
                new Color(0.6f, 0.95f, 0.7f, 1f));

            var bubble = rig.AddComponent<DialogueBubble>();
            bubble.connection = rig.GetComponent<CopilotConnection>();
            bubble.label = subtitle;

            // 装配方（CopilotBootstrap）也要拿到 bubble —— 漏掉这条它就不会 Rebind
            var bootstrap = rig.GetComponent<CopilotBootstrap>();
            if (bootstrap != null) bootstrap.bubble = bubble;

            var tutorial = rig.AddComponent<TutorialDirector>();
            tutorial.connection = rig.GetComponent<CopilotConnection>();
            tutorial.stepLabel = stepLabel;
            tutorial.resultLabel = resultLabel;
            tutorial.bubble = bubble;

            var hud = rig.GetComponent<HudStatus>();
            hud.trustBar = barFill;
            hud.trustLabel = trustLabel;
            hud.emotionLabel = emotionLabel;
            hud.toastLabel = toast;
            hud.recordIndicator = recordDot;
        }

        // ---- 小工具 ---------------------------------------------------

        static Material MakeMaterial(Color color, Color? emission = null)
        {
            var shader = Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Lit");
            var mat = new Material(shader) { color = color };
            if (emission.HasValue)
            {
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", emission.Value);
            }
            return mat;
        }

        static GameObject Box(string name, Transform parent, Vector3 localPos, Vector3 scale, Material mat,
            bool withCollider = true)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            if (!withCollider) UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
            return go;
        }

        static GameObject Cylinder(string name, Transform parent, Vector3 localPos, Vector3 scale, Material mat,
            Quaternion? localRotation = null, bool withCollider = true)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            if (localRotation.HasValue) go.transform.localRotation = localRotation.Value;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            if (!withCollider) UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
            return go;
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

            var cockpit = FindInScene<CockpitCamera>(scene);
            var cam = FindInScene<Camera>(scene);
            Requires(cam != null, "场景里没有 Camera", problems);
            Requires(cockpit != null, "场景里没有第一人称副驾相机 CockpitCamera", problems);

            // 第一人称的核心：相机必须是 Car 的子物体（坐在车里），而不是车外跟随
            Requires(cam != null && cam.transform.parent != null && cam.transform.parent.name == CarName,
                "相机没挂在车内（应为 Car 的子物体）", problems);

            Requires(FindInScene<SteeringWheel>(scene) != null, "场景里没有方向盘", problems);
            Requires(FindInScene<DriverAvatar>(scene) != null, "驾驶位上没有学员模型", problems);

            var tutorial = FindInScene<TutorialDirector>(scene);
            Requires(tutorial != null, "场景里没有教学关导演 TutorialDirector", problems);
            Requires(tutorial != null && tutorial.stepLabel != null, "TutorialDirector.stepLabel 未连", problems);
            Requires(CountInScene(scene, "Dash") >= 1, "没有仪表台，车内是空的", problems);

            // 场景内容：确认 P9 的城市几何真的生成了（空场景也能「校验通过」就没意义了）
            Requires(CountInScene(scene, "Building") >= 10, "楼群数量不足（<10），城市没生成", problems);
            Requires(CountInScene(scene, "StreetLight") >= 8, "路灯数量不足（<8）", problems);
            Requires(CountInScene(scene, "LaneDash") >= 20, "车道虚线数量不足（<20）", problems);
            Requires(CountInScene(scene, "ParkedCar") >= 1, "没有路边停车", problems);

            if (problems.Length > 0) throw Fail(problems);

            Debug.Log("[SceneBuilder] 校验通过：场景结构完整（" + scene.path + "）");
        }

        // ---------------------------------------------------------------- 预览图

        /// <summary>
        /// 把场景渲染成一张 PNG。
        ///
        /// 存在的理由：`-batchmode` 下没法用眼睛看，容易被「结构校验通过」骗过去 ——
        /// 引用都连上了不代表画面对。这里用 <c>Camera.Render</c> 渲到 RenderTexture 再存盘，
        /// 人和 CI 都能直接看图。<see cref="CockpitCamera"/> 在编辑模式不跑，相机就停在
        /// 生成时摆的副驾座上，所以看到的就是开局镜头。
        /// </summary>
        public static string Capture()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var cam = FindInScene<Camera>(scene);
            if (cam == null) throw new InvalidOperationException("场景里没有 Camera");

            // 一张不够：副驾往前看时，坐在你左边的学员**本来就不在视野里**（跟真车一样）。
            // 所以再渲一张「扭头看学员」的，否则没法确认人形到底建出来没有。
            var cockpit = FindInScene<CockpitCamera>(scene);
            float restYaw = cockpit != null ? cockpit.restYaw : 0f;
            var forward = CaptureView(cam, Quaternion.Euler(0f, restYaw, 0f), PreviewRelativePath);
            CaptureView(cam, Quaternion.Euler(0f, -58f, 0f), DriverPreviewRelativePath);
            return forward;
        }

        /// <summary>把相机摆到指定姿态渲一帧存 PNG，然后还原。</summary>
        static string CaptureView(Camera cam, Quaternion localRotation, string relativePath)
        {
            const int width = 1280;
            const int height = 720;
            var path = RepoPath(relativePath);

            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var previousTarget = cam.targetTexture;
            var previousActive = RenderTexture.active;
            var previousRotation = cam.transform.localRotation;

            try
            {
                cam.transform.localRotation = localRotation;
                cam.targetTexture = rt;
                cam.Render();

                RenderTexture.active = rt;
                var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
                tex.Apply();

                var png = tex.EncodeToPNG();
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                File.WriteAllBytes(path, png);

                UnityEngine.Object.DestroyImmediate(tex);
                Debug.Log($"[SceneBuilder] 预览图：{path}（{png.Length / 1024} KB）");
                return path;
            }
            finally
            {
                cam.transform.localRotation = previousRotation;
                cam.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        static string RepoPath(string relative)
        {
            var unityProject = Directory.GetParent(Application.dataPath)?.FullName;
            var repo = unityProject == null ? null : Directory.GetParent(unityProject)?.FullName;
            if (repo == null) throw new InvalidOperationException("无法定位仓库根目录");
            return Path.Combine(repo, relative);
        }

        // ---- 通用 ---------------------------------------------------

        static T FindInScene<T>(Scene scene) where T : Component
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                var found = root.GetComponentInChildren<T>(true);
                if (found != null) return found;
            }
            return null;
        }

        static int CountInScene(Scene scene, string name)
        {
            int count = 0;
            foreach (var root in scene.GetRootGameObjects())
            {
                count += CountNamed(root.transform, name);
            }
            return count;
        }

        static int CountNamed(Transform t, string name)
        {
            int count = t.name == name ? 1 : 0;
            for (int i = 0; i < t.childCount; i++)
            {
                count += CountNamed(t.GetChild(i), name);
            }
            return count;
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