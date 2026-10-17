using System;
using System.Collections.Generic;
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
    /// 生成「开箱可玩」的场景。
    ///
    /// 为什么用代码生成而不是手搭：手搭一次要挂十来个组件、连二十来处引用，还容易漏；
    /// 生成器是可重跑的**代码**，改一处结构重新生成即可。
    ///
    /// **场地有两种（`VenueKind`）**：
    /// - `TrainingGround`（默认，写 `Assets/Scenes/Greybox.unity`）：**封闭低速的驾校练习场** ——
    ///   水泥场地 + 直线练习道 + 直角弯区 + 倒车入库位 + 侧方停车位 + S 弯 + 围栏。
    ///   科目一教学关用它（issue #2：新手不该一上来就被扔进大马路）。
    /// - `MainRoad`（备用菜单，写 `Assets/Scenes/Road.unity`）：原来的 4 车道城市马路 + 路口 + 楼群 + 路灯 + 路边停车。
    ///   **留给科目三**，平时不生成 —— 那些数值是踩坑换来的（例如「标线 y 必须 > 0.012，否则埋进沥青里看不见」），不要删。
    ///
    /// 产物（以训练场为例）：
    /// - `TrainingGround`：场地 + 练习道标线 + 库位 + 围栏 + 锥桶 + `RoadGeometry`
    /// - `Car`：Rigidbody + 4×WheelCollider + `VehicleController`（`VehiclePhysicsProfile` 资产）
    /// - 相机：`CockpitCamera`（第一人称副驾座，是 `Car` 的子物体）
    /// - `CopilotRig`：`CopilotConnection` + 执行/状态/音频/UI 全套 + `CopilotBootstrap`
    /// - `HUD`：信任条 / 情绪 / 字幕 / 飘条 / 教学步骤 / 仪表 / 玩法横幅（UGUI，无需 TextMeshPro）
    ///
    /// 用法（编辑器菜单或 batchmode）：
    /// - `副驾指令 / 生成灰盒场景`            → `SceneBuilder.BuildAndExit`（训练场）
    /// - `副驾指令 / 生成主路场景（备用）`    → 写 `Road.unity`，供科目三
    /// - `副驾指令 / 校验灰盒场景`            → `SceneBuilder.ValidateAndExit`（断言引用都连上了）
    /// - `副驾指令 / 渲染场景预览图`          → `SceneBuilder.CaptureAndExit`（渲 PNG，供人/CI 看画面）
    /// </summary>
    public static class SceneBuilder
    {
        /// <summary>场地类型。默认训练场；主路留给科目三，靠备用菜单生成。</summary>
        public enum VenueKind
        {
            TrainingGround,
            MainRoad,
        }

        const string SceneDir = "Assets/Scenes";
        const string ScenePath = SceneDir + "/Greybox.unity";
        /// <summary>主路场景（备用，科目三用）—— 平时不生成、不占 Build Settings。</summary>
        const string MainRoadScenePath = SceneDir + "/Road.unity";
        const string ProfilePath = "Assets/VehiclePhysicsProfile.asset";
        const string PreviewRelativePath = "logs/unity_scene_preview.png";
        const string DriverPreviewRelativePath = "logs/unity_scene_preview_driver.png";
        const string SettlePreviewRelativePath = "logs/unity_scene_preview_settle.png";

        const string RigName = "CopilotRig";
        const string CarName = "Car";

        /// <summary>固定随机种子：每次生成的楼群/道具布局完全一致，便于对比与回归。</summary>
        const int Seed = 20261011;

        // 路面参数（车辆与道具都按这些摆放，改这里就整体改）—— **主路场地专用**
        const float RoadHalfWidth = 8f;
        const float RoadLength = 360f;
        const float RoadCenterZ = 120f;
        const float CrossZ = 120f;
        const float CrossHalfWidth = 8f;

        // ---- 训练场参数（科目一练习场：封闭、低速、无车流）----------------------
        // 场地是一大块水泥地；车道标线画在 x = ±3.6 / 0（三条直线练习道），
        // 尽头是直角弯区（复用 RoadGeometry.crossCenterZ 表达「进弯就不判横向偏移」）。
        const float YardHalfWidth = 32f;      // x 半宽 → 场地宽 64m（库位在 ±16、S 弯到 ±28，都放得下；
        //                                      收窄是为了让围栏近一点 —— 90m 宽时开局镜头根本看不见边界，"封闭感"就没了）
        const float YardLength = 240f;        // z 长度
        const float YardCenterZ = 80f;        // 场地中心 z
        const float YardMarkY = 0.012f;       // 标线高度：必须**高于**地面顶面（y=0），否则埋进水泥里看不见 —— 踩过
        const float YardLaneCenterX = 3.6f;   // 右侧车道中心 —— 车就从这里起步
        const float StraightEndZ = 92f;       // 直线练习道尽头（停止线）
        const float TurnZoneZ = 112f;         // 直角弯区中心（= RoadGeometry.crossCenterZ）
        const float TurnZoneHalf = 12f;       // 弯区半宽
        const float BayWidth = 6f;            // 倒车入库位宽
        const float BayDepth = 12f;           // 倒车入库位深

        // 共用材质（一次构建内复用，避免几百个材质实例）
        static Material _road, _laneSolid, _laneDash, _sidewalk, _curb, _pad, _fence;
        static Material _bldA, _bldB, _bldC, _window, _roof;
        static Material _pole, _lamp;
        static Material _body, _glass, _tire, _rim, _lightFront, _lightRear, _cone;
        static Material _bumper, _grille, _chrome, _plate;
        static Material _dash, _console, _seat, _cabinTrim, _skin, _hair, _cloth, _mirror;
        static Material _eyeWhite, _pupil, _brow, _lip, _shoe, _collar;

        [MenuItem("副驾指令/生成灰盒场景")]
        public static void BuildFromMenu() => BuildFromMenu(VenueKind.TrainingGround);

        [MenuItem("副驾指令/生成主路场景（备用，科目三用）")]
        public static void BuildMainRoadFromMenu() => BuildFromMenu(VenueKind.MainRoad);

        static void BuildFromMenu(VenueKind venue)
        {
            Build(venue);
            AssetDatabase.SaveAssets();
            string path = VenuePath(venue);
            EditorSceneManager.OpenScene(path);
            Debug.Log("[SceneBuilder] 已生成并打开 " + path);
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

        static string VenuePath(VenueKind venue)
            => venue == VenueKind.MainRoad ? MainRoadScenePath : ScenePath;

        public static void BuildAndExit() => RunAndExit(() => Build());
        /// <summary>生成主路场景（备用，科目三用）。</summary>
        public static void BuildMainRoadAndExit() => RunAndExit(() => Build(VenueKind.MainRoad));
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

        /// <summary>生成默认场地（训练场）到 <c>Greybox.unity</c>。</summary>
        public static void Build() => Build(VenueKind.TrainingGround);

        public static void Build(VenueKind venue)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            CreatePalette();
            SetupEnvironment();
            CreateLight();

            var profile = EnsureProfile();
            var ground = venue == VenueKind.MainRoad ? CreateMainRoadVenue() : CreateTrainingGround();
            // 起步位置要落在**某条车道的中心线上**：训练场是 3 条练习道（中心 ±3.6/0），主路是 4 条（中心 ±1.8/±5.4）
            float startX = venue == VenueKind.MainRoad ? 1.8f : YardLaneCenterX;
            var car = CreateVehicle(profile, ground.transform, startX);   // 相机随车一起建（第一人称副驾座）
            var rig = CreateRig(car, ground.GetComponent<RoadGeometry>(), venue);
            CreateHud(rig);

            if (!AssetDatabase.IsValidFolder(SceneDir))
            {
                AssetDatabase.CreateFolder("Assets", "Scenes");
            }

            string path = VenuePath(venue);
            bool saved = EditorSceneManager.SaveScene(scene, path);
            if (!saved) throw new InvalidOperationException("场景保存失败：" + path);

            // **只有默认场地写进 Build Settings** —— 主路是备用场景，别让它抢掉默认启动场景
            if (venue == VenueKind.TrainingGround)
            {
                EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            }

            Debug.Log("[SceneBuilder] 场景已生成：" + path + "（场地 " + venue + "）");
        }

        static void CreatePalette()
        {
            _road = MakeMaterial(new Color(0.20f, 0.21f, 0.23f));
            _laneSolid = MakeMaterial(new Color(0.88f, 0.88f, 0.85f));
            _laneDash = MakeMaterial(new Color(0.92f, 0.92f, 0.88f));
            _sidewalk = MakeMaterial(new Color(0.55f, 0.55f, 0.54f));
            _curb = MakeMaterial(new Color(0.72f, 0.72f, 0.70f));
            _pad = MakeMaterial(new Color(0.44f, 0.45f, 0.47f));     // 训练场水泥地
            _fence = MakeMaterial(new Color(0.56f, 0.58f, 0.61f));   // 围栏

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

            // 车身高细节件（P 轮加：让车身不是一个色块）
            _bumper = MakeMaterial(new Color(0.17f, 0.17f, 0.18f));   // 保险杠/侧裙：深色塑料
            _grille = MakeMaterial(new Color(0.07f, 0.07f, 0.08f));   // 格栅/灯罩/门缝：近黑
            _chrome = MakeMaterial(new Color(0.82f, 0.83f, 0.86f));   // 轮辋中心盖/饰条
            _plate = MakeMaterial(new Color(0.90f, 0.90f, 0.84f));    // 车牌

            // 车内（第一人称副驾视角要看到的东西）
            _dash = MakeMaterial(new Color(0.16f, 0.16f, 0.17f));
            _console = MakeMaterial(new Color(0.11f, 0.11f, 0.12f));
            _seat = MakeMaterial(new Color(0.20f, 0.19f, 0.20f));
            _cabinTrim = MakeMaterial(new Color(0.34f, 0.34f, 0.35f));
            _mirror = MakeMaterial(new Color(0.10f, 0.11f, 0.13f));
            _skin = MakeMaterial(new Color(0.93f, 0.80f, 0.70f));
            _hair = MakeMaterial(new Color(0.15f, 0.11f, 0.10f));
            _cloth = MakeMaterial(new Color(0.42f, 0.52f, 0.68f));   // 学员的衣服

            // 学员的五官与零件（P 轮加：方盒躯干 + 圆柱脸无论怎么摆都读作积木）
            _eyeWhite = MakeMaterial(new Color(0.95f, 0.95f, 0.93f));
            _pupil = MakeMaterial(new Color(0.10f, 0.09f, 0.10f));
            _brow = MakeMaterial(new Color(0.13f, 0.10f, 0.09f));
            _lip = MakeMaterial(new Color(0.72f, 0.35f, 0.34f));
            _shoe = MakeMaterial(new Color(0.16f, 0.16f, 0.18f));
            _collar = MakeMaterial(new Color(0.30f, 0.38f, 0.52f));
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

        /// <summary>
        /// **训练场（科目一，默认场地）**：封闭、低速、无车流的驾校练习场。
        ///
        /// 布局（x 向右、z 向前，场地 90×240）：
        /// - 水泥场地 `Ground` + 四周围栏 `Fence`
        /// - 三条直线练习道（标线 `LaneMark`，车道中心 x = −3.6 / 0 / +3.6），从 z=−20 到停止线 z=92
        /// - 停止线 `StopLine`（z=92）与尽头横向练习道 —— 左边/右边真有弯可拐
        /// - 左侧两个**倒车入库位**、右侧两个**侧方停车位**（`BayLine`）
        /// - 右后区用锥桶摆一条 S 弯（纯观感，不做判定）
        /// - `HazardCar`（「忘了刹车」用，默认不激活）
        ///
        /// 动机（issue #2）：新手第一课不该被直接扔进 4 车道大马路。
        /// </summary>
        static GameObject CreateTrainingGround()
        {
            var yard = new GameObject("TrainingGround");
            var parent = yard.transform;

            // 水泥地：y = −0.5 让**顶面正好落在 y = 0**（与主路一致，车轮不会陷进去）
            Box("Ground", parent, new Vector3(0f, -0.5f, YardCenterZ),
                new Vector3(YardHalfWidth * 2f, 1f, YardLength), _pad);

            CreateFence(parent);
            CreateTrainingLanes(parent);
            CreateParkingBays(parent);
            CreateTrainingCones(parent);
            CreateHazardCar(parent);

            // 道路几何的**唯一真源**（玩法检测要用）。
            // 训练场里 `laneCenterXs` = 三条直线练习道的中心；
            // `crossCenterZ` 从「路口」泛化成「**弯道/交叉练习区中心**」—— 进弯区就不判横向偏移
            // （`VehicleSensors` 的那套判据天然适用，见 RoadGeometry 注释）。
            var road = yard.AddComponent<RoadGeometry>();
            road.roadForward = Vector3.forward;
            road.laneCenterXs = new[] { -3.6f, 0f, 3.6f };
            road.laneWidthM = 3.6f;
            road.crossCenterZ = TurnZoneZ;
            road.crossHalfExtentM = TurnZoneHalf;

            return yard;
        }

        /// <summary>四周围栏 —— 把练习场圈起来，一眼就是"封闭场地"。</summary>
        static void CreateFence(Transform parent)
        {
            const float h = 1.6f;
            const float t = 0.3f;
            float halfW = YardHalfWidth + 1.5f;
            float z0 = YardCenterZ - YardLength / 2f;
            float z1 = YardCenterZ + YardLength / 2f;

            for (int side = -1; side <= 1; side += 2)
            {
                Box("Fence", parent, new Vector3(side * halfW, h / 2f, YardCenterZ),
                    new Vector3(t, h, YardLength + 3f), _fence);
            }
            for (int end = -1; end <= 1; end += 2)
            {
                Box("Fence", parent, new Vector3(0f, h / 2f, end < 0 ? z0 : z1),
                    new Vector3(halfW * 2f + t, h, t), _fence);
            }
        }

        /// <summary>
        /// 直线练习道标线 + 停止线 + 尽头横向练习道。
        /// ⚠️ 标线一律 **`withCollider: false`** —— 它们是"漆"，不是障碍物：
        /// 带碰撞体会让车轮在 2cm 高的薄片上颠一下，而且每次接触都可能被当成撞车。
        /// </summary>
        static void CreateTrainingLanes(Transform parent)
        {
            const float zStart = -20f;
            float len = StraightEndZ - zStart;               // z ∈ [−20, 92]
            float midZ = (StraightEndZ + zStart) / 2f;

            // 车道分隔虚线（x = ±1.8）+ 两侧边线（x = ±5.4）→ 车道中心 −3.6 / 0 / +3.6
            for (int side = -1; side <= 1; side += 2)
            {
                Box("LaneMark", parent, new Vector3(side * 1.8f, YardMarkY, midZ),
                    new Vector3(0.16f, 0.02f, len), _laneDash, withCollider: false);
                Box("LaneMark", parent, new Vector3(side * 5.4f, YardMarkY, midZ),
                    new Vector3(0.18f, 0.02f, len), _laneSolid, withCollider: false);
            }

            // 停止线：减速这一步的视觉锚点
            Box("StopLine", parent, new Vector3(0f, YardMarkY, StraightEndZ),
                new Vector3(11.2f, 0.02f, 0.6f), _laneSolid, withCollider: false);

            // 尽头横向练习道：左转/右转完真的有路可走（也让 `crossCenterZ` 在画面上说得通）
            Box("LaneMark", parent, new Vector3(0f, YardMarkY, TurnZoneZ),
                new Vector3(YardHalfWidth * 1.5f, 0.02f, 0.16f), _laneDash, withCollider: false);
        }

        /// <summary>倒车入库位（左侧两个）+ 侧方停车位（右侧两个），都是画出来的库位边框。</summary>
        static void CreateParkingBays(Transform parent)
        {
            for (int i = 0; i < 2; i++)
            {
                float z = 30f + i * 18f;
                CreateBayOutline(parent, new Vector3(-16f, 0f, z), BayWidth, BayDepth);   // 倒车入库
                CreateBayOutline(parent, new Vector3(14f, 0f, z), BayWidth, 8f);           // 侧方停车
            }
        }

        /// <summary>库位边框（四条边线）。同样不带碰撞体。</summary>
        static void CreateBayOutline(Transform parent, Vector3 center, float width, float depth)
        {
            const float t = 0.14f;
            float hw = width / 2f;
            float hd = depth / 2f;

            for (int side = -1; side <= 1; side += 2)
            {
                Box("BayLine", parent, new Vector3(center.x + side * hw, YardMarkY, center.z),
                    new Vector3(t, 0.02f, depth), _laneSolid, withCollider: false);
                Box("BayLine", parent, new Vector3(center.x, YardMarkY, center.z + side * hd),
                    new Vector3(width, 0.02f, t), _laneSolid, withCollider: false);
            }
        }

        /// <summary>S 弯：两排锥桶摆出的曲线路径（纯观感，不做判定）。</summary>
        static void CreateTrainingCones(Transform parent)
        {
            for (int i = 0; i < 6; i++)
            {
                float z = 108f + i * 13f;
                float wobble = Mathf.Sin(i * 1.1f) * 6f;
                Cylinder("Cone", parent, new Vector3(22f + wobble, 0.35f, z),
                    new Vector3(0.5f, 0.35f, 0.5f), _cone);
            }
        }

        /// <summary>
        /// **主路场地（备用，留给科目三）**：4 车道城市马路 + 路口 + 楼群 + 路灯 + 锥桶 + 路边停车。
        ///
        /// 平时不由 `BuildAndExit` 生成（走菜单「生成主路场景（备用）」），**保留是因为那些数值是踩坑换来的**
        /// （车道分隔虚线在 x=±3.6、边线 x=±7.2 → 车道中心 ±1.8/±5.4；标线 y 必须 &gt; 0 否则埋进沥青里）。
        /// </summary>
        static GameObject CreateMainRoadVenue()
        {
            var city = new GameObject("City");
            var rng = new System.Random(Seed);

            CreateRoad(city.transform);
            CreateBuildings(city.transform, rng);
            CreateStreetLights(city.transform);
            CreateRoadProps(city.transform, rng);
            CreateHazardCar(city.transform);

            var road = city.AddComponent<RoadGeometry>();
            road.roadForward = Vector3.forward;
            road.laneCenterXs = new[] { -5.4f, -1.8f, 1.8f, 5.4f };
            road.laneWidthM = 3.6f;
            road.crossCenterZ = CrossZ;
            road.crossHalfExtentM = CrossHalfWidth;

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

        /// <summary>主路场地的道具（路口锥桶 + 路边停车）—— 只给科目三用。</summary>
        static void CreateRoadProps(Transform parent, System.Random rng)
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

        /// <summary>
        /// 「前方那辆车」（失误「忘了刹车」用）：平时藏在场地外且**不激活**，
        /// 由 `MistakeRunner` 在失误开演时才摆到车头正前方 —— 常驻会在正常教学流程里伏击玩家。
        ///
        /// ⚠️ **两种场地都要建**：`MistakeRunner.PlaceHazardAhead` / `Validate` / `GameplaySelfTest` 都依赖它。
        /// </summary>
        static void CreateHazardCar(Transform parent)
        {
            var hazard = new GameObject("HazardCar");
            hazard.transform.SetParent(parent, false);
            // 摆在两种场地**都不在的行车范围**里（x=−120 离训练场 ±32 和主路 ±8 都很远）——
            // 它反正总会被 PlaceHazardAhead 挪到车头正前方，这里只要保证"藏着的时候不挡路"。
            hazard.transform.localPosition = new Vector3(-120f, 0f, YardCenterZ + YardLength / 2f);
            BuildCarBody(hazard.transform, new Color(0.62f, 0.34f, 0.30f), withWheels: true);
            hazard.SetActive(false);
        }

        // ---- 车辆 -----------------------------------------------------

        static GameObject CreateVehicle(VehiclePhysicsProfile profile, Transform parent, float startX)
        {
            var car = new GameObject(CarName);
            car.transform.position = new Vector3(startX, 0.0f, 0f); // 落在车道中心线上（靠右行驶 → 右车道）

            var body = car.AddComponent<Rigidbody>();
            body.mass = 1400f;
            body.drag = 0.02f;          // 2022.3 是 drag/angularDrag（Unity 6 才改名 linearDamping）
            body.angularDrag = 0.4f;

            // 玩家的车要「敞篷」：不开顶盖的实心 Cabin，改由车内件（顶棚/A柱/门板）拼，
            // 否则第一人称相机就坐在实心盒子里，朝前看是一片黑。
            // 玩家的轮子由 AddWheel（WheelCollider + 可视轮）负责 —— 这里**不要**再建静态轮，
            // 否则会和可动轮重叠（同一位置两套网格 → 加辐条后会明显闪烁/z-fighting）
            BuildCarBody(car.transform, new Color(0.72f, 0.18f, 0.16f), withWheels: false, openCabin: true);

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

            const float torsoY = 0.24f;

            // 肩斜的收分躯干：上宽下窄 + 肩，别再用等宽方盒
            Capsule("Torso", avatar.transform, new Vector3(0f, torsoY, 0f), new Vector3(0.46f, 0.30f, 0.30f), _cloth,
                withCollider: false);
            Sphere("ShoulderL", avatar.transform, new Vector3(-0.17f, torsoY + 0.13f, 0f), new Vector3(0.20f, 0.17f, 0.22f), _cloth, withCollider: false);
            Sphere("ShoulderR", avatar.transform, new Vector3(0.17f, torsoY + 0.13f, 0f), new Vector3(0.20f, 0.17f, 0.22f), _cloth, withCollider: false);
            Cylinder("Collar", avatar.transform, new Vector3(0f, torsoY + 0.20f, 0f), new Vector3(0.30f, 0.03f, 0.24f), _collar, withCollider: false);

            // 大腿（第一人称视角偶尔能瞥见）与鞋
            for (int sx = -1; sx <= 1; sx += 2)
            {
                Capsule("Thigh", avatar.transform, new Vector3(sx * 0.11f, torsoY - 0.22f, 0.14f), new Vector3(0.15f, 0.16f, 0.15f), _cloth,
                    Quaternion.Euler(70f, 0f, 0f), withCollider: false);
            }

            // ---- 头 ----
            var head = new GameObject("Head").transform;
            head.SetParent(avatar.transform, false);
            head.localPosition = new Vector3(0f, 0.60f, 0.01f);

            // 颅骨：压扁的球（球体头部才像人；圆柱头会一直读作积木）
            Sphere("Face", head, new Vector3(0f, 0f, 0.01f), new Vector3(0.20f, 0.23f, 0.21f), _skin, withCollider: false);
            // 下颌收窄
            Sphere("Jaw", head, new Vector3(0f, -0.075f, 0.03f), new Vector3(0.15f, 0.11f, 0.16f), _skin, withCollider: false);
            Cylinder("Neck", head, new Vector3(0f, -0.135f, 0f), new Vector3(0.09f, 0.045f, 0.09f), _skin, withCollider: false);

            // ⚠️ 五官必须**凸出颅骨球面**，否则会被球"吞"进去 ——
            // 第一版眼/鼻/嘴都放在球内部（颅骨最前缘 z≈0.115，而它们只到 ~0.10），
            // 结果截图里脸一片空白、头就是个黑球。
            for (int sx = -1; sx <= 1; sx += 2)
            {
                Sphere("EyeWhite", head, new Vector3(sx * 0.042f, 0.008f, 0.105f), new Vector3(0.046f, 0.034f, 0.032f), _eyeWhite, withCollider: false);
                Sphere("Pupil", head, new Vector3(sx * 0.042f, 0.008f, 0.117f), new Vector3(0.020f, 0.020f, 0.016f), _pupil, withCollider: false);
                Box("Brow", head, new Vector3(sx * 0.045f, 0.046f, 0.106f), new Vector3(0.044f, 0.009f, 0.014f), _brow, withCollider: false);
            }
            Sphere("NoseTip", head, new Vector3(0f, -0.014f, 0.112f), new Vector3(0.026f, 0.026f, 0.024f), _skin, withCollider: false);
            Box("Mouth", head, new Vector3(0f, -0.055f, 0.108f), new Vector3(0.052f, 0.013f, 0.014f), _lip, withCollider: false);

            // 头发做成"盖在后脑上"的壳：**前缘必须留在额头之后**（z<0.07），否则会把整张脸罩住
            Sphere("Hair", head, new Vector3(0f, 0.062f, -0.038f), new Vector3(0.216f, 0.19f, 0.205f), _hair, withCollider: false);
            for (int sx = -1; sx <= 1; sx += 2)
            {
                Sphere("HairSide", head, new Vector3(sx * 0.098f, 0.012f, -0.022f), new Vector3(0.05f, 0.20f, 0.18f), _hair, withCollider: false);
                Sphere("HairTail", head, new Vector3(sx * 0.085f, -0.12f, -0.10f), new Vector3(0.07f, 0.12f, 0.09f), _hair, withCollider: false);
            }

            // ---- 双手伸向方向盘（DriverAvatar 让它们跟着盘转）----
            var leftArm = BuildArm(avatar.transform, -0.13f);
            var rightArm = BuildArm(avatar.transform, 0.13f);

            var driver = avatar.AddComponent<DriverAvatar>();
            driver.wheel = wheel;
            driver.head = head;
            driver.leftArm = leftArm;
            driver.rightArm = rightArm;
        }

        /// <summary>一条手臂：上臂（肩→肘）+ 前臂（肘→盘）+ 手。挂在 <c>ArmL/ArmR</c> 组下，由 DriverAvatar 驱动。</summary>
        static Transform BuildArm(Transform parent, float x)
        {
            var arm = new GameObject(x < 0f ? "ArmL" : "ArmR").transform;
            arm.SetParent(parent, false);
            arm.localPosition = new Vector3(x, 0.30f, 0.16f);
            arm.localRotation = Quaternion.Euler(-20f, 0f, x < 0f ? -6f : 6f);

            // 上臂：从肩往前下方
            Capsule("UpperArm", arm, new Vector3(0f, -0.03f, 0.11f), new Vector3(0.11f, 0.13f, 0.11f), _cloth,
                Quaternion.Euler(72f, 0f, 0f), withCollider: false);
            Sphere("Elbow", arm, new Vector3(0f, -0.01f, 0.24f), new Vector3(0.105f, 0.105f, 0.105f), _cloth, withCollider: false);

            // 前臂：肘 → 方向盘
            Capsule("Forearm", arm, new Vector3(0f, 0f, 0.36f), new Vector3(0.095f, 0.11f, 0.095f), _cloth,
                Quaternion.Euler(86f, 0f, 0f), withCollider: false);
            Sphere("Hand", arm, new Vector3(0f, 0.01f, 0.49f), new Vector3(0.115f, 0.095f, 0.13f), _skin, withCollider: false);

            return arm;
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

            // ⚠️ **碰撞体只留一个**（BodyLower）。其余全是视觉件，一律 withCollider:false ——
            // 否则碰撞外形会跟着造型变化，连带改掉玩法层的撞车判定与「前方距离」测量。
            Box("BodyLower", root, new Vector3(0f, 0.55f, 0f), new Vector3(1.80f, 0.44f, 4.15f), bodyMat);

            // 腰线以上的上车身（略窄，做出收分）
            Box("BodyUpper", root, new Vector3(0f, 0.93f, -0.05f), new Vector3(1.74f, 0.34f, 3.55f), bodyMat, withCollider: false);

            // 前悬 / 车头：向车头收窄 + 保险杠下沿
            Box("Nose", root, new Vector3(0f, 0.66f, 1.95f), new Vector3(1.66f, 0.30f, 0.60f), bodyMat, withCollider: false);
            Box("FrontBumper", root, new Vector3(0f, 0.48f, 2.16f), new Vector3(1.78f, 0.30f, 0.24f), _bumper, withCollider: false);
            Box("Grille", root, new Vector3(0f, 0.68f, 2.25f), new Vector3(1.05f, 0.18f, 0.06f), _grille, withCollider: false);

            // 机舱盖（带一点前倾）与后备箱盖
            Tilted("Hood", root, new Vector3(0f, 0.99f, 1.32f), new Vector3(1.62f, 0.13f, 1.55f), bodyMat, -3f);
            Tilted("Trunk", root, new Vector3(0f, 0.99f, -1.45f), new Vector3(1.60f, 0.13f, 1.15f), bodyMat, 2.5f);

            // 后悬 + 后保险杠
            Box("Tail", root, new Vector3(0f, 0.68f, -1.98f), new Vector3(1.64f, 0.32f, 0.55f), bodyMat, withCollider: false);
            Box("RearBumper", root, new Vector3(0f, 0.50f, -2.16f), new Vector3(1.76f, 0.30f, 0.24f), _bumper, withCollider: false);

            // 轮拱（四个鼓包，让轮子"长在车身上"而不是贴在外面）
            const float halfTrack = 0.85f;
            for (int sx = -1; sx <= 1; sx += 2)
            {
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    Sphere("Fender", root, new Vector3(sx * (halfTrack - 0.16f), 0.60f, sz * 1.35f),
                        new Vector3(0.62f, 0.42f, 0.86f), bodyMat, withCollider: false);
                }
            }

            // 侧裙 / 门缝：一条深色腰线把车身"切开"，避免整块车漆糊成一片
            for (int sx = -1; sx <= 1; sx += 2)
            {
                Box("SideSkirt", root, new Vector3(sx * 0.905f, 0.42f, 0.05f), new Vector3(0.03f, 0.16f, 3.2f), _bumper, withCollider: false);
                Box("DoorSeam", root, new Vector3(sx * 0.912f, 0.80f, -0.15f), new Vector3(0.02f, 0.42f, 0.03f), _grille, withCollider: false);
            }

            if (!openCabin)
            {
                // 整块驾驶舱（路边停车/危险车用；玩家的车由 CreateInterior 拼中空座舱）
                Box("Cabin", root, new Vector3(0f, 1.18f, -0.30f), new Vector3(1.58f, 0.50f, 1.95f), bodyMat, withCollider: false);

                // 玻璃都带倾角，不再是插在盒子上的平板
                Tilted("Windshield", root, new Vector3(0f, 1.14f, 0.80f), new Vector3(1.46f, 0.52f, 0.05f), _glass, -24f);
                Tilted("RearGlass", root, new Vector3(0f, 1.14f, -1.30f), new Vector3(1.44f, 0.46f, 0.05f), _glass, 20f);
                for (int sx = -1; sx <= 1; sx += 2)
                {
                    Tilted("SideGlass", root, new Vector3(sx * 0.80f, 1.16f, 0.05f), new Vector3(0.04f, 0.40f, 1.55f), _glass, 0f);
                    Box("APillar", root, new Vector3(sx * 0.80f, 1.14f, 0.92f), new Vector3(0.06f, 0.52f, 0.10f), bodyMat, withCollider: false);
                    Box("CPillar", root, new Vector3(sx * 0.80f, 1.14f, -1.20f), new Vector3(0.06f, 0.48f, 0.12f), bodyMat, withCollider: false);
                }
            }

            // 灯组：灯罩 + 发光件（前后各一对）
            for (int sx = -1; sx <= 1; sx += 2)
            {
                Box("HeadHouse", root, new Vector3(sx * 0.62f, 0.80f, 2.16f), new Vector3(0.44f, 0.20f, 0.10f), _grille, withCollider: false);
                Box(sx < 0 ? "HeadL" : "HeadR", root, new Vector3(sx * 0.62f, 0.80f, 2.22f), new Vector3(0.34f, 0.13f, 0.04f), _lightFront, withCollider: false);

                Box("TailHouse", root, new Vector3(sx * 0.60f, 0.84f, -2.17f), new Vector3(0.42f, 0.18f, 0.09f), _grille, withCollider: false);
                Box(sx < 0 ? "TailL" : "TailR", root, new Vector3(sx * 0.60f, 0.84f, -2.22f), new Vector3(0.32f, 0.12f, 0.04f), _lightRear, withCollider: false);
            }

            // 后视镜（杆 + 镜片）
            for (int sx = -1; sx <= 1; sx += 2)
            {
                Cylinder("MirrorStalk", root, new Vector3(sx * 0.95f, 1.14f, 0.55f), new Vector3(0.05f, 0.10f, 0.05f), _bumper,
                    Quaternion.Euler(0f, 0f, sx * 35f), withCollider: false);
                Box(sx < 0 ? "MirrorL" : "MirrorR", root, new Vector3(sx * 1.06f, 1.16f, 0.52f), new Vector3(0.09f, 0.15f, 0.06f), _mirror, withCollider: false);
            }

            // 车牌
            Box("PlateFront", root, new Vector3(0f, 0.58f, 2.29f), new Vector3(0.44f, 0.12f, 0.03f), _plate, withCollider: false);
            Box("PlateRear", root, new Vector3(0f, 0.60f, -2.29f), new Vector3(0.44f, 0.12f, 0.03f), _plate, withCollider: false);

            if (!withWheels) return;

            // 静态车轮（路边停车 / 危险车用；玩家车的轮子由 WheelCollider 管）
            for (int sx = -1; sx <= 1; sx += 2)
            {
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    WheelVisual(root, new Vector3(sx * halfTrack, 0.35f, sz * 1.35f), 0.35f);
                }
            }
        }

        /// <summary>带绕 X 轴倾角的盒子（风挡/机舱盖要用；<see cref="Box"/> 没有旋转参数）。</summary>
        static GameObject Tilted(string name, Transform parent, Vector3 localPos, Vector3 scale, Material mat, float pitchDeg)
        {
            var go = Box(name, parent, localPos, scale, mat, withCollider: false);
            go.transform.localRotation = Quaternion.Euler(pitchDeg, 0f, 0f);
            return go;
        }

        static void WheelVisual(Transform parent, Vector3 localPos, float radius)
        {
            var go = new GameObject("WheelVisual");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);

            BuildWheelMesh(go.transform, radius);
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
            // （与静态车共用同一套轮网格：胎 + 轮辋 + 辐条 + 中心盖）
            var visual = new GameObject("Mesh");
            visual.transform.SetParent(wheel.transform, false);
            visual.transform.localPosition = Vector3.zero;
            // Unity 的 Cylinder 沿 Y 轴；转 90° 让轮轴朝 X，子件再按半径缩放
            visual.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            BuildWheelMesh(visual.transform, radius);

            mesh = visual.transform;
            return collider;
        }

        /// <summary>
        /// 一套轮子的可视网格。**在已旋转 90° 的组里建**：组的局部 Y 就是轮轴。
        /// 静态车（<see cref="WheelVisual"/>）与玩家车（<see cref="AddWheel"/>）共用它，
        /// 否则两种车的轮子长得不一样。
        /// </summary>
        static void BuildWheelMesh(Transform wheelGroup, float radius)
        {
            Cylinder("Tire", wheelGroup, Vector3.zero, new Vector3(radius * 2f, 0.12f, radius * 2f), _tire, withCollider: false);
            Cylinder("Rim", wheelGroup, Vector3.zero, new Vector3(radius * 0.62f, 0.14f, radius * 0.62f), _rim, withCollider: false);

            // 辐条：绕轮轴（组的局部 Y）均布几根细条，让轮辋看起来是"轮辋"而不是白饼
            const int spokes = 5;
            for (int i = 0; i < spokes; i++)
            {
                var spoke = Box("Spoke", wheelGroup, Vector3.zero,
                    new Vector3(0.055f, 0.15f, radius * 1.15f), _rim, withCollider: false);
                spoke.transform.localRotation = Quaternion.Euler(0f, i * 360f / spokes, 0f);
                spoke.transform.localPosition = Quaternion.Euler(0f, i * 360f / spokes, 0f)
                    * new Vector3(0f, 0f, radius * 0.55f);
            }

            Cylinder("Hub", wheelGroup, Vector3.zero, new Vector3(radius * 0.26f, 0.17f, radius * 0.26f), _chrome, withCollider: false);
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

        static GameObject CreateRig(GameObject car, RoadGeometry road, VenueKind venue)
        {
            var rig = new GameObject(RigName);

            var connection = rig.AddComponent<CopilotConnection>();
            var vehicle = car.GetComponent<VehicleController>();

            // 传感器（只测量：横向偏移 / 前方障碍距离 / 撞车）。
            // 道路几何挂在场地根物体上 —— 注意车是**根物体**（CreateVehicle 的 parent 参数其实没生效），
            // 所以不能靠 car.transform.parent 去拿，必须显式传进来。
            var sensors = car.AddComponent<VehicleSensors>();
            sensors.vehicle = vehicle;
            sensors.road = road;
            sensors.hazardCar = road != null ? road.transform.Find("HazardCar") : null;

            // 封闭练习场里没有"车道"可言（车会开去倒车库位/S 弯），要关掉独立的压线上报 ——
            // 否则**正常练一次变道**（横向偏移 > 1.0m 持续 400ms）就会被记一次压线（−6 信任 / −6 安全分），
            // 玩家只会觉得"我明明没压线凭什么扣我分"。主路场地仍保留该判据。
            if (venue == VenueKind.TrainingGround) sensors.standaloneLaneDeparture = false;

            var gameState = rig.AddComponent<GameStateManager>();
            gameState.connection = connection;
            gameState.vehicle = vehicle;

            var executor = rig.AddComponent<ActionExecutor>();
            executor.connection = connection;
            executor.vehicle = vehicle;

            // 玩法层驱动（剧本失误 → 注入 → 判定 → 结算）。
            // tutorial / bubble 在 CreateHud 里回填（那两个组件是在 CreateHud 才建的）。
            var runner = rig.AddComponent<MistakeRunner>();
            runner.connection = connection;
            runner.executor = executor;
            runner.gameState = gameState;
            runner.vehicle = vehicle;
            runner.sensors = sensors;

            var tts = rig.AddComponent<TtsPlayer>();
            tts.connection = connection;

            // 本地语音库：玩法事件（她慌了/松口气）要**出声**，不能只靠服务端回合
            var localVoice = rig.AddComponent<LocalVoiceLib>();
            runner.ttsPlayer = tts;
            runner.localVoice = localVoice;

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
            bootstrap.road = road;
            bootstrap.sensors = sensors;
            bootstrap.mistakeRunner = runner;

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

            NewText("Hint", root, "按住空格说话 · Q/E 或按住鼠标右键扭头看学员 · Enter 结算 · R 重开一局", 20,
                TextAnchor.LowerLeft, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(30f, 30f), new Vector2(1100f, 60f), new Color(1f, 1f, 1f, 0.6f));

            // 教学关：当前步骤 + 结果播报（科目一 · 训练场第一课）。
            // 从三行扩到**四行**（issue #2 要求每一步看得见「目标 / 成功条件」）：
            // 关卡+步数 / 本节要做什么 / 目标 / 照着念。所以区域往下延长（顶部不动，
            // 免得和上面的信任条/飘条打架），字号 24 → 22 让四行放得下。
            var stepLabel = NewText("StepLabel", root, "", 22, TextAnchor.UpperLeft,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(30f, -306f), new Vector2(900f, -108f),
                new Color(0.95f, 0.97f, 1f, 0.92f));
            stepLabel.verticalOverflow = VerticalWrapMode.Overflow;

            // 结果行：过关写"教学关完成"，做错写「动作不对 / 方向不对：为什么」（TutorialDirector 会换色）
            var resultLabel = NewText("ResultLabel", root, "", 20, TextAnchor.UpperLeft,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(30f, -348f), new Vector2(900f, -310f),
                new Color(0.6f, 0.95f, 0.7f, 1f));

            // 驾驶仪表：车速 + 方向盘转角
            // 副驾视角看不到仪表盘，这两个数必须自己画 —— 否则没法判断她是不是要出事
            var speedLabel = NewText("SpeedLabel", root, "0", 56, TextAnchor.LowerRight,
                new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-190f, 66f), new Vector2(-40f, 146f), Color.white);

            NewText("SpeedCaption", root, "km/h", 20, TextAnchor.LowerRight,
                new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-260f, 36f), new Vector2(-192f, 72f),
                new Color(1f, 1f, 1f, 0.7f));

            var speedBarBg = NewImage("SpeedBarBg", root, new Color(0f, 0f, 0f, 0.55f));
            Anchor(speedBarBg.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-260f, 22f), new Vector2(-40f, 42f));

            var speedBar = NewImage("SpeedBar", root, new Color(0.45f, 0.8f, 1f, 1f));
            Anchor(speedBar.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-260f, 22f), new Vector2(-40f, 42f));
            speedBar.type = Image.Type.Filled;
            speedBar.fillMethod = Image.FillMethod.Horizontal;
            speedBar.fillAmount = 0f;

            // 方向盘转角：一条轨道 + 一个滑块，中心 = 回正
            var steerTrack = NewImage("SteerTrack", root, new Color(0f, 0f, 0f, 0.55f));
            Anchor(steerTrack.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-260f, 6f), new Vector2(-40f, 26f));

            var steerKnob = NewImage("SteerKnob", root, new Color(1f, 0.85f, 0.4f, 1f));
            Anchor(steerKnob.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-156f, 0f), new Vector2(-144f, 32f));

            var steerLabel = NewText("SteerLabel", root, "方向回正", 20, TextAnchor.LowerRight,
                new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-470f, 4f), new Vector2(-266f, 34f),
                new Color(1f, 0.9f, 0.7f, 1f));

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

            var driveHud = rig.AddComponent<DriveHud>();
            driveHud.vehicle = bootstrap != null ? bootstrap.vehicle : null;
            // 方向盘是 Car 的子物体，从车体往下找最稳（不用 FindObjectOfType 扫全场景）
            driveHud.steeringWheel = driveHud.vehicle != null
                ? driveHud.vehicle.GetComponentInChildren<SteeringWheel>(true)
                : null;
            driveHud.speedLabel = speedLabel;
            driveHud.speedFill = speedBar;
            driveHud.steerTrack = steerTrack.rectTransform;
            driveHud.steerKnob = steerKnob.rectTransform;
            driveHud.steerLabel = steerLabel;
            driveHud.maxWheelAngle = driveHud.steeringWheel != null ? driveHud.steeringWheel.maxAngle : 180f;

            // ---- 玩法层 UI：警告横幅 / 实时分数 / 结算面板 ----

            // 警告横幅：她在犯错、等你开口。
            // ⚠️ 位置必须**避开左上角的四行教学块**（Y 轮扩行后它占到 y≈306px）：
            // 横幅是后建的兄弟节点，会**盖在**结果行上面（实测把「方向不对：…」那句话吃掉了）。
            // 所以放在屏幕中部偏上（y 从顶部 410~518px），既显眼又不打架。
            var bannerBg = NewImage("MistakeBannerBg", root, new Color(0.95f, 0.6f, 0.15f, 0.85f));
            Anchor(bannerBg.rectTransform, new Vector2(0.25f, 0.52f), new Vector2(0.75f, 0.62f), Vector2.zero, Vector2.zero);

            var bannerLabel = NewText("MistakeBanner", root, "", 34, TextAnchor.MiddleCenter,
                new Vector2(0.25f, 0.52f), new Vector2(0.75f, 0.62f), Vector2.zero, Vector2.zero, Color.white);
            bannerLabel.horizontalOverflow = HorizontalWrapMode.Wrap;
            bannerBg.enabled = false;
            bannerLabel.enabled = false;

            var scoreLabel = NewText("ScoreLabel", root, "", 22, TextAnchor.MiddleRight,
                new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-330f, -112f), new Vector2(-30f, -80f),
                new Color(1f, 0.92f, 0.7f, 0.95f));

            // 结算面板（默认隐藏；六步走完后弹，或按 Enter 手动弹）
            // 高度按内容定（title/stars/两列 6 行明细）—— 实测 560px 高时下半部空掉一半，收紧到 400
            var panelGo = new GameObject("SettlementPanel", typeof(RectTransform));
            panelGo.transform.SetParent(root, false);
            Anchor(panelGo.GetComponent<RectTransform>(), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(-360f, -200f), new Vector2(360f, 200f));

            var panelBg = panelGo.AddComponent<Image>();
            panelBg.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
            panelBg.type = Image.Type.Sliced;
            panelBg.color = new Color(0.05f, 0.06f, 0.09f, 0.92f);

            var settleTitle = NewText("SettlementTitle", panelGo.transform, "教学关结算", 40,
                TextAnchor.UpperCenter, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, -95f), new Vector2(0f, -45f), Color.white);

            var settleStars = NewText("SettlementStars", panelGo.transform, "", 52, TextAnchor.UpperCenter,
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -179f), new Vector2(0f, -99f),
                new Color(1f, 0.85f, 0.4f));

            var settleStats = NewText("SettlementStats", panelGo.transform, "", 26, TextAnchor.UpperLeft,
                new Vector2(0f, 1f), new Vector2(0.5f, 1f), new Vector2(40f, -330f), new Vector2(-10f, -191f),
                Color.white);
            settleStats.verticalOverflow = VerticalWrapMode.Overflow;

            var settleItems = NewText("SettlementItems", panelGo.transform, "", 24, TextAnchor.UpperLeft,
                new Vector2(0.5f, 1f), new Vector2(1f, 1f), new Vector2(10f, -330f), new Vector2(-40f, -191f),
                new Color(0.8f, 0.85f, 0.95f));
            settleItems.horizontalOverflow = HorizontalWrapMode.Wrap;
            settleItems.verticalOverflow = VerticalWrapMode.Overflow;

            panelGo.SetActive(false);

            // tutorial / bubble 是这里的局部量，所以玩法驱动的引用只能在这里回填
            // （和上面 bootstrap.bubble 的做法一致）
            var runner = rig.GetComponent<MistakeRunner>();
            if (runner != null)
            {
                runner.tutorial = tutorial;
                runner.bubble = bubble;
                runner.Rebind();            // 引用到位后重新绑定（AddComponent 时引用还是空的）
            }

            var gameplayHud = rig.AddComponent<GameplayHud>();
            gameplayHud.runner = runner;
            gameplayHud.tutorial = tutorial;
            gameplayHud.bannerBg = bannerBg;
            gameplayHud.bannerLabel = bannerLabel;
            gameplayHud.scoreLabel = scoreLabel;
            gameplayHud.settlementPanel = panelGo;
            gameplayHud.settlementTitle = settleTitle;
            gameplayHud.settlementStats = settleStats;
            gameplayHud.settlementStars = settleStars;
            gameplayHud.settlementItems = settleItems;

            if (runner != null) runner.gameplayHud = gameplayHud;

            // 装配方也要拿到玩法 HUD（同 bootstrap.bubble 的回填理由）
            var bootstrapForHud = rig.GetComponent<CopilotBootstrap>();
            if (bootstrapForHud != null) bootstrapForHud.gameplayHud = gameplayHud;
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

        static GameObject Sphere(string name, Transform parent, Vector3 localPos, Vector3 scale, Material mat,
            Quaternion? localRotation = null, bool withCollider = true)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            if (localRotation.HasValue) go.transform.localRotation = localRotation.Value;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            if (!withCollider) UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
            return go;
        }

        /// <summary>
        /// 胶囊（Unity 的 Capsule 高 2 单位、沿 Y 轴）。
        /// 有它才能做出"像人"的肢体 —— 方盒手臂无论怎么摆都读作积木。
        /// </summary>
        static GameObject Capsule(string name, Transform parent, Vector3 localPos, Vector3 scale, Material mat,
            Quaternion? localRotation = null, bool withCollider = true)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
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

            // ---- 学员的五官/身体（P 轮的"高细节"必须真的生成了，别退回一堆方块）----
            Requires(CountInScene(scene, "Head") >= 1, "学员没有头", problems);
            Requires(CountInScene(scene, "Face") >= 1, "学员没有脸（球体颅骨）", problems);
            Requires(CountInScene(scene, "Hair") >= 1, "学员没有头发", problems);
            Requires(CountInScene(scene, "EyeWhite") >= 2, "学员没有眼睛", problems);
            Requires(CountInScene(scene, "Pupil") >= 2, "学员没有瞳孔", problems);
            Requires(CountInScene(scene, "Mouth") >= 1, "学员没有嘴", problems);
            Requires(CountInScene(scene, "Neck") >= 1, "学员没有脖子", problems);
            Requires(CountInScene(scene, "Torso") >= 1, "学员没有躯干", problems);
            Requires(CountInScene(scene, "Elbow") >= 2 && CountInScene(scene, "Forearm") >= 2,
                "学员的手臂没有肘/前臂（不是人形）", problems);
            Requires(CountInScene(scene, "Hand") >= 2, "学员没有手", problems);
            Requires(CountInScene(scene, "Brow") >= 2, "学员没有眉毛", problems);

            // ---- 车身高细节件 ----
            Requires(CountInScene(scene, "Nose") >= 1, "车身没有车头前悬", problems);
            Requires(CountInScene(scene, "Grille") >= 1, "车身没有格栅", problems);
            Requires(CountInScene(scene, "FrontBumper") >= 1 && CountInScene(scene, "RearBumper") >= 1,
                "车身没有前后保险杠", problems);
            Requires(CountInScene(scene, "HeadL") >= 1 && CountInScene(scene, "HeadR") >= 1,
                "车头灯不齐", problems);
            Requires(CountInScene(scene, "TailL") >= 1 && CountInScene(scene, "TailR") >= 1,
                "车尾灯不齐", problems);
            Requires(CountInScene(scene, "MirrorL") >= 1 && CountInScene(scene, "MirrorR") >= 1,
                "车身没有后视镜", problems);
            Requires(CountInScene(scene, "PlateFront") >= 1 && CountInScene(scene, "PlateRear") >= 1,
                "车身没有车牌", problems);
            // 辐条：至少一台车的四个轮子（4 轮 × 5 辐条）
            Requires(CountInScene(scene, "Spoke") >= 20, "轮辋没有辐条（还是白饼轮）", problems);
            Requires(CountInScene(scene, "Fender") >= 4, "车身没有轮拱", problems);

            // 车的整体尺寸要在合理区间（造型改坏了最常见的样子就是"塌成一堆"）
            var carBounds = ChildRendererBounds(vehicle != null ? vehicle.gameObject : null);
            if (carBounds.HasValue)
            {
                var size = carBounds.Value.size;
                Requires(size.z > 3.5f && size.z < 5.5f, $"车长不合理：{size.z:0.0}m", problems);
                Requires(size.y > 1.2f && size.y < 2.4f, $"车高不合理：{size.y:0.0}m", problems);
                Requires(size.x > 1.5f && size.x < 2.6f, $"车宽不合理：{size.x:0.0}m", problems);
            }
            else
            {
                Requires(false, "拿不到车体包围盒（车上一个 Renderer 都没有？）", problems);
            }

            var driveHud = FindInScene<DriveHud>(scene);
            Requires(driveHud != null, "场景里没有驾驶仪表 DriveHud（车速/转角看不见就没法及时干预）", problems);
            if (driveHud != null)
            {
                Requires(driveHud.vehicle != null, "DriveHud.vehicle 未连", problems);
                Requires(driveHud.steeringWheel != null, "DriveHud.steeringWheel 未连", problems);
                Requires(driveHud.speedLabel != null, "DriveHud.speedLabel 未连", problems);
                Requires(driveHud.steerKnob != null && driveHud.steerTrack != null, "DriveHud 转角滑块/轨道未连", problems);
                Requires(driveHud.steerLabel != null, "DriveHud.steerLabel 未连", problems);
            }

            var tutorial = FindInScene<TutorialDirector>(scene);
            Requires(tutorial != null, "场景里没有教学关导演 TutorialDirector", problems);
            Requires(tutorial != null && tutorial.stepLabel != null, "TutorialDirector.stepLabel 未连", problems);
            Requires(CountInScene(scene, "Dash") >= 1, "没有仪表台，车内是空的", problems);

            // ---- 玩法层（主动犯错 + 结算）----
            var road = FindInScene<RoadGeometry>(scene);
            Requires(road != null, "场景里没有道路几何 RoadGeometry（玩法检测靠它）", problems);
            if (road != null)
            {
                Requires(road.laneCenterXs != null && road.laneCenterXs.Length == 3,
                    "RoadGeometry.laneCenterXs 不是 3 条练习道（车道数变了检测会错位）", problems);
            }

            var sensors = FindInScene<VehicleSensors>(scene);
            Requires(sensors != null, "Car 上没有 VehicleSensors（横向偏移/前方距离/撞车测不到）", problems);
            if (sensors != null)
            {
                Requires(sensors.vehicle != null, "VehicleSensors.vehicle 未连", problems);
                Requires(sensors.road != null, "VehicleSensors.road 未连", problems);
                Requires(sensors.hazardCar != null, "VehicleSensors.hazardCar 未连（「忘了刹车」没有前方车辆）", problems);
            }

            var runner = FindInScene<MistakeRunner>(scene);
            Requires(runner != null, "场景里没有玩法驱动 MistakeRunner（教学进度台账 / 重开 / 本地语音都靠它）", problems);
            if (runner != null)
            {
                Requires(runner.connection != null, "MistakeRunner.connection 未连", problems);
                Requires(runner.executor != null, "MistakeRunner.executor 未连", problems);
                Requires(runner.gameState != null, "MistakeRunner.gameState 未连", problems);
                Requires(runner.vehicle != null, "MistakeRunner.vehicle 未连", problems);
                Requires(runner.sensors != null, "MistakeRunner.sensors 未连", problems);
                Requires(runner.tutorial != null, "MistakeRunner.tutorial 未连（拿不到教学进度）", problems);
                Requires(runner.bubble != null, "MistakeRunner.bubble 未连（她慌了你听不到）", problems);
            }

            var gameplayHud = FindInScene<GameplayHud>(scene);
            Requires(gameplayHud != null, "场景里没有玩法 HUD GameplayHud（警告/分数/结算看不见）", problems);
            if (gameplayHud != null)
            {
                Requires(gameplayHud.runner != null, "GameplayHud.runner 未连", problems);
                Requires(gameplayHud.bannerBg != null && gameplayHud.bannerLabel != null,
                    "GameplayHud 警告横幅未连", problems);
                Requires(gameplayHud.scoreLabel != null, "GameplayHud.scoreLabel 未连", problems);
                Requires(gameplayHud.settlementPanel != null, "GameplayHud 结算面板未连", problems);
                Requires(gameplayHud.settlementStats != null && gameplayHud.settlementStars != null,
                    "GameplayHud 结算面板的文字未连", problems);
            }

            Requires(CountInScene(scene, "HazardCar") >= 1, "没有「前方那辆车」 HazardCar", problems);
            Requires(CountInScene(scene, "SettlementPanel") >= 1, "没有结算面板 SettlementPanel", problems);
            Requires(CountInScene(scene, "MistakeBanner") >= 1, "没有警告横幅 MistakeBanner", problems);

            // 场景内容：确认**训练场**几何真的生成了（空场景也能「校验通过」就没意义了）。
            // 训练场 = 水泥地 + 练习道标线 + 库位 + 围栏；**不该再有楼群**（那是主路场地的观感，issue #2 要它消失）。
            Requires(CountInScene(scene, "Ground") >= 1, "没有训练场水泥地（Ground）", problems);
            Requires(CountInScene(scene, "LaneMark") >= 4, "练习道标线数量不足（<4）", problems);
            Requires(CountInScene(scene, "BayLine") >= 8, "库位标线数量不足（<8，倒车入库/侧方停车没画出来）", problems);
            Requires(CountInScene(scene, "Fence") >= 4, "围栏数量不足（<4），场地不是封闭的", problems);
            Requires(CountInScene(scene, "StopLine") >= 1, "没有停止线（减速那一步没有视觉锚点）", problems);
            Requires(CountInScene(scene, "Building") == 0, "训练场里不该有楼群（那是「大马路」观感，issue #2 要它消失）", problems);

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

            // 玩法 UI 平时是隐藏的（横幅要等失误、结算面板要等关卡结束），预览图里就看不见 ——
            // 而排版问题**只有看图才能发现**。所以临时点亮再还原。
            // ⚠️ 三张图**各管一件事**：把结算面板也塞进"扭头看学员"那张的话，它会把人挡住，
            //    结果学员的模型反而没法检查（第一版就是这么踩的）。
            var gameplayHud = FindInScene<GameplayHud>(scene);
            ShowcaseBanner(gameplayHud, true);
            ShowcaseTutorial(scene, true);
            var forward = CaptureView(cam, Quaternion.Euler(0f, restYaw, 0f), PreviewRelativePath);
            ShowcaseTutorial(scene, false);
            ShowcaseBanner(gameplayHud, false);

            // 扭头看学员：**不放面板**，专门检查人形建模（-70° 才把她摆到画面中间附近）
            // 编辑模式她只会朝前坐，相机只能看到侧脸 —— 临时把她的头扭向副驾（运行时她说话时就是这样），
            // 否则「五官到底建出来没有」这件事**看不出来**。
            ShowcaseDriverFace(scene, true);
            CaptureView(cam, Quaternion.Euler(0f, -70f, 0f), DriverPreviewRelativePath);
            ShowcaseDriverFace(scene, false);

            // 结算面板：单独一张，专门检查排版
            ShowcaseSettlement(gameplayHud, true);
            CaptureView(cam, Quaternion.Euler(0f, restYaw, 0f), SettlePreviewRelativePath);
            ShowcaseSettlement(gameplayHud, false);

            return forward;
        }

        /// <summary>预览图专用：把学员的头扭向副驾（运行时她说话时就是这样），好让人看清五官。</summary>
        static void ShowcaseDriverFace(Scene scene, bool on)
        {
            var avatar = FindInScene<DriverAvatar>(scene);
            if (avatar == null || avatar.head == null) return;
            avatar.head.localRotation = on ? Quaternion.Euler(0f, 58f, 0f) : Quaternion.identity;
        }

        /// <summary>
        /// 预览图专用：给教学提示块填一份**代表性文本**。
        ///
        /// 编辑模式下 `TutorialDirector.Start` 不跑 → `Refresh()` 没执行 → 这两行是空的，
        /// 于是**四行提示的排版永远无法从预览图里检查**（而排版问题只有看图才发现）。
        /// 这里按 `TutorialDirector.Refresh` 的真实格式填样例，渲完再清掉。
        /// </summary>
        static void ShowcaseTutorial(Scene scene, bool on)
        {
            var tutorial = FindInScene<TutorialDirector>(scene);
            if (tutorial == null) return;

            if (tutorial.stepLabel != null)
            {
                tutorial.stepLabel.text = on
                    ? "【科目一】训练场 · 第一课　5/9\n"
                      + "本节：向左变道 —— 让她往左挪一条道。变道本身不带左右，方向靠你原话说清。\n"
                      + "目标：真的往左挪（转向为负）\n"
                      + "照着念：「向左变道」"
                    : string.Empty;
            }

            if (tutorial.resultLabel != null)
            {
                tutorial.resultLabel.text = on ? "方向不对：她挪反了方向 —— 变道时把「左」说清楚。" : string.Empty;
            }
        }

        /// <summary>预览图专用：临时点亮/还原「警告横幅 + 实时分数」。</summary>
        static void ShowcaseBanner(GameplayHud hud, bool on)
        {
            if (hud == null) return;

            if (hud.bannerBg != null) hud.bannerBg.enabled = on;
            if (hud.bannerLabel != null)
            {
                hud.bannerLabel.enabled = on;
                hud.bannerLabel.text = on ? "她偏出车道了！快喊她回正\n快喊她！剩 3 秒" : string.Empty;
            }
            if (hud.scoreLabel != null) hud.scoreLabel.text = on ? "本局评分 82" : string.Empty;
        }

        /// <summary>预览图专用：临时点亮/还原结算面板（填示例数据，方便看排版）。</summary>
        static void ShowcaseSettlement(GameplayHud hud, bool on)
        {
            if (hud == null || hud.settlementPanel == null) return;

            hud.settlementPanel.SetActive(on);
            if (!on) return;

            if (hud.settlementStars != null) hud.settlementStars.text = "★★☆";
            if (hud.settlementStats != null)
            {
                hud.settlementStats.text = "安全  22 / 40\n及时干预  20 / 40\n教学完成  20 / 20\n总分  82 / 100";
            }
            if (hud.settlementItems != null)
            {
                hud.settlementItems.text = "撞车：1（-12）\n压线：1（-6）\n及时干预：1（+20）\n"
                    + "漏掉的失误：1\n教学步：6/6（+20）\n最终信任：52";
            }
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

            // HUD 是 ScreenSpaceOverlay：由 UI 系统合成、**不经相机**，所以原样渲染的预览图里看不到它。
            // 临时切成 ScreenSpaceCamera（贴到相机前 0.25m，比仪表台近，不会被挡住），渲完还原。
            var switched = new List<(Canvas canvas, RenderMode mode, Camera world)>();
            foreach (var canvas in UnityEngine.Object.FindObjectsOfType<Canvas>(true))
            {
                if (canvas.renderMode != RenderMode.ScreenSpaceOverlay) continue;
                switched.Add((canvas, canvas.renderMode, canvas.worldCamera));
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = cam;
                canvas.planeDistance = 0.25f;
            }

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
                Debug.Log($"[SceneBuilder] 预览图：{path}（{png.Length / 1024} KB，含 HUD {switched.Count} 个画布）");
                return path;
            }
            finally
            {
                foreach (var (canvas, mode, world) in switched)
                {
                    if (canvas == null) continue;
                    canvas.renderMode = mode;
                    canvas.worldCamera = world;
                }
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

        /// <summary>把物体及其所有子物体的 Renderer 包围盒并起来（断言车的整体尺寸用）。</summary>
        static Bounds? ChildRendererBounds(GameObject go)
        {
            if (go == null) return null;

            var renderers = go.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return null;

            var bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            return bounds;
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