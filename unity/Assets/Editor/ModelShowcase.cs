using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Copilot.EditorTools
{
    /// <summary>
    /// 把用户给的真模型单独摆出来拍几张图（**不生成整个场景**）。
    ///
    /// 为什么需要它：AI 生成的 FBX 里，**朝向、原点在哪、到底包含哪些部位**全都是未知数
    /// （体检只量得到尺寸 0.83×0.86×0.69 与「没有骨骼」）。直接塞进车里再拍预览，
    /// 相机常常整个落在模型内部 → 只能拍到一片黑，什么也判断不了。
    /// 先在空场景里把「她朝哪边 / 坐姿还是站姿 / 内饰是个什么件」拍清楚，再决定怎么摆。
    ///
    /// 用法（batchmode）：
    ///   powershell -ExecutionPolicy Bypass -File tools\unity_batch.ps1 -Method Copilot.EditorTools.ModelShowcase.ShowAndExit
    /// 输出（仓库根 logs/，均已 gitignore）：model_showcase_{minus_z,plus_z,plus_x,face}.png
    /// </summary>
    public static class ModelShowcase
    {
        const string SisterAsset = "Assets/Models/AI/sister/sister.fbx";
        const string InteriorAsset = "Assets/Models/AI/interior/interior.fbx";

        public static void ShowAndExit()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var lightGo = new GameObject("Sun", typeof(Light));
            var light = lightGo.GetComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.15f;
            light.color = new Color(1f, 0.97f, 0.92f);
            lightGo.transform.rotation = Quaternion.Euler(38f, 150f, 0f);
            RenderSettings.ambientLight = new Color(0.42f, 0.44f, 0.5f);

            Show("sister", SisterAsset);
            Show("interior", InteriorAsset);

            EditorApplication.Exit(0);
        }

        /// <summary>
        /// **一次只摆一个模型**、相机按实测包围盒自动取景 —— 两个摆在一起拍过一次：
        /// 相机在 +X 那侧时，前面的模型把后面的整个挡住（内外饰与学员糊成一坨，等于白拍）。
        /// 三个方向各一张：−Z 侧 / +Z 侧（判断朝向必须从两头各看一次）/ +X 侧。
        /// </summary>
        static void Show(string tag, string assetPath)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null)
            {
                Debug.LogWarning($"[ModelShowcase] 没有 {assetPath}");
                return;
            }

            var ground = new GameObject("Ground").transform;
            ground.position = Vector3.zero;
            var holder = SceneBuilder.PlaceModel(prefab, tag, ground, 1f);
            var b = SceneBuilder.BoundsInSpace(ground, holder);

            Shoot(ground, b, new Vector3(0f, 0.35f, -1f), $"logs/model_{tag}_minus_z.png");
            Shoot(ground, b, new Vector3(0f, 0.35f, 1f), $"logs/model_{tag}_plus_z.png");
            Shoot(ground, b, new Vector3(1f, 0.35f, -0.15f), $"logs/model_{tag}_plus_x.png");

            Object.DestroyImmediate(holder);
            Object.DestroyImmediate(ground.gameObject);
        }

        /// <summary>相机沿 <paramref name="dir"/> 退到刚好装下 <paramref name="b"/>（含 15% 余量）的位置。</summary>
        static void Shoot(Transform space, Bounds b, Vector3 dir, string relativePath)
        {
            var camGo = new GameObject("ShotCam", typeof(Camera));
            var cam = camGo.GetComponent<Camera>();
            cam.fieldOfView = 40f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.13f, 0.15f, 0.18f);
            cam.nearClipPlane = 0.02f;

            float radius = b.extents.magnitude;
            float dist = radius / Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.15f;
            var target = space.TransformPoint(b.center);
            camGo.transform.position = target + dir.normalized * dist;
            camGo.transform.LookAt(target);

            var rt = new RenderTexture(1024, 768, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            cam.Render();

            RenderTexture.active = rt;
            var tex = new Texture2D(1024, 768, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0f, 0f, 1024f, 768f), 0, 0);
            tex.Apply();

            var png = tex.EncodeToPNG();
            var path = RepoPath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllBytes(path, png);
            Debug.Log($"[ModelShowcase] {path}（bounds={b.size} dist={dist:0.00}）");

            Object.DestroyImmediate(tex);
            cam.targetTexture = null;
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(camGo);
        }

        static string RepoPath(string relative)
        {
            var unityProject = Directory.GetParent(Application.dataPath)?.FullName;
            var repo = unityProject == null ? null : Directory.GetParent(unityProject)?.FullName;
            if (repo == null) throw new System.InvalidOperationException("无法定位仓库根目录");
            return Path.Combine(repo, relative);
        }
    }
}
