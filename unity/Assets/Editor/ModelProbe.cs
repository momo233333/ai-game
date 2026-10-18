using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Copilot.EditorTools
{
    /// <summary>
    /// 一次性体检：把用户给的「妹妹 / 内饰」两个 FBX 导入后打印几何与材质概况。
    ///
    /// 为什么要它：AI 生成的 FBX 尺度不一定是米（可能是 cm，也可能 0.01），
    /// 也**不一定有骨骼** —— 摆放用的缩放、以及「能不能做骨骼动画（转头/张嘴）」都得先量出来再定。
    /// 猜一个缩放直接摆进场景，多半得到一个 18 米高的学员，或者一个没贴图的白模。
    ///
    /// 用法（batchmode）：
    ///   powershell -ExecutionPolicy Bypass -File tools\unity_batch.ps1 -Method Copilot.EditorTools.ModelProbe.RunAndExit
    /// 输出：logs/model_probe.txt（仓库根）＋ 同一份内容写进 Unity 日志。
    /// </summary>
    public static class ModelProbe
    {
        static readonly string[] Targets =
        {
            "Assets/Models/AI/sister/sister.fbx",
            "Assets/Models/AI/interior/interior.fbx",
        };

        public static void RunAndExit()
        {
            // 高度图要打射线：**先清成空场景**，否则场景里的地面/车/围栏会先被射线打到（量出来全是它们的顶面）
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var sb = new StringBuilder();
            foreach (var path in Targets) Probe(path, sb);

            var outPath = RepoPath("logs/model_probe.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
            Debug.Log(sb.ToString());
            EditorApplication.Exit(0);
        }

        static void Probe(string path, StringBuilder sb)
        {
            sb.AppendLine("==== " + path + " ====");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ImportRecursive);

            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null)
            {
                sb.AppendLine("  !! 载入失败（路径不存在或导入出错）");
                return;
            }

            // 贴图：FBX 里引用的是相对路径 `output.fbm/xxx.png`，而用户只给了 .fbx ——
            // 先试把**内嵌**贴图抽出来（`ModelImporter.ExtractTextures`，注意不是 AssetDatabase 上的），
            // 抽不出来再想别的办法。
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            var fbm = Path.ChangeExtension(path, null) + "/output.fbm";
            if (importer != null)
            {
                try
                {
                    var before = Directory.Exists(fbm) ? Directory.GetFiles(fbm, "*.png").Length : 0;
                    importer.ExtractTextures(fbm);
                    var after = Directory.Exists(fbm) ? Directory.GetFiles(fbm, "*.png").Length : 0;
                    sb.AppendLine($"  ExtractTextures → {fbm}（{before} → {after} 张 png）");
                }
                catch (System.Exception ex)
                {
                    sb.AppendLine($"  ExtractTextures 失败：{ex.GetType().Name}: {ex.Message}");
                }
            }

            // 抽贴图会触发重导，先前那个引用已失效
            go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null)
            {
                sb.AppendLine("  !! 抽贴图后载入失败");
                return;
            }

            if (importer != null)
            {
                sb.AppendLine($"  导入设置：globalScale={importer.globalScale} fileScale={importer.useFileScale} " +
                              $"animationType={importer.animationType} importAnimation={importer.importAnimation} " +
                              $"blendShapes={importer.importBlendShapes} materials={importer.materialImportMode}");
            }

            int meshCount = 0, tris = 0, skinned = 0;
            var union = new Bounds();
            bool has = false;

            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r is SkinnedMeshRenderer) skinned++;

                var mf = r.GetComponent<MeshFilter>();
                string meshInfo = "（无 MeshFilter）";
                if (mf != null && mf.sharedMesh != null)
                {
                    meshCount++;
                    var mesh = mf.sharedMesh;
                    int t = mesh.triangles.Length / 3;
                    tris += t;
                    meshInfo = $"mesh={mesh.name} verts={mesh.vertexCount} tris={t} localSize={mesh.bounds.size}";
                }

                // 世界（资产根在原点）包围盒 —— 决定摆放缩放的就是它
                if (has) union.Encapsulate(r.bounds); else { union = r.bounds; has = true; }

                sb.AppendLine($"  [{r.name}] {meshInfo} tex={(r.sharedMaterial != null && r.sharedMaterial.mainTexture != null ? r.sharedMaterial.mainTexture.name : "none")}");
            }

            sb.AppendLine($"  合计：Mesh {meshCount} · 三角面 {tris} · SkinnedMeshRenderer {skinned} · 子物体 {go.GetComponentsInChildren<Transform>(true).Length}");
            if (has) sb.AppendLine($"  包围盒 center={union.center} size={union.size}");
            sb.AppendLine();

            // ---- 逐 submesh 量部件 ----
            // 模型是"一整套东西塞进一个 mesh"（座椅 + 仪表台 + 方向盘 + 中控…）。
            // 想按"真车的座垫高度 / 仪表台顶面高度"来摆相机与方向盘，就得知道**各部件在哪儿** ——
            // submesh ↔ 材质就是天然的部件切分，量出来比看图估计靠谱得多。
            sb.AppendLine("  -- 逐 submesh（材质 / 三角面 / 包围盒，资产空间，单位 m）--");
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                var mf = r.GetComponent<MeshFilter>();
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null) continue;

                var verts = mesh.vertices;
                var mats = r.sharedMaterials;
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    var tri = mesh.GetTriangles(s);
                    if (tri.Length == 0) continue;

                    var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                    var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                    for (int i = 0; i < tri.Length; i++)
                    {
                        var v = r.transform.localToWorldMatrix.MultiplyPoint3x4(verts[tri[i]]);
                        min = Vector3.Min(min, v);
                        max = Vector3.Max(max, v);
                    }

                    string matName = s < mats.Length && mats[s] != null ? mats[s].name : "?";
                    var size = max - min;
                    sb.AppendLine($"    [{s}] {matName,-24} tris={tri.Length / 3,-7} " +
                                  $"x[{min.x:0.000},{max.x:0.000}] y[{min.y:0.000},{max.y:0.000}] z[{min.z:0.000},{max.z:0.000}] size={size}");
                }
            }
            sb.AppendLine();

            HeightMap(go, path, sb);
        }

        /// <summary>
        /// **顶面高度分桶统计**：装临时 MeshCollider，在 x/z 细网格（2cm）上往下打射线，
        /// 把每个命中点按**2cm 高度**分桶，报每桶的点数与 (x,z) 覆盖范围。
        ///
        /// 为什么不用 ASCII 高度图：那个要看几十行数字自己找规律。分桶之后**一眼就能认出部件** ——
        /// "0.50 上有两片对称的 x 正负各一、z 同段" = 左右两个**座垫**；"0.55 上一条横贯的窄带" = **仪表台顶面**；
        /// "0.12 上一大片" = **地板**；"0.76 上一小块居中" = **后视镜**。
        /// 相机坐多高、方向盘摆哪儿，全看这些数 —— 所以必须量，不能猜。
        /// </summary>
        static void HeightMap(GameObject asset, string path, StringBuilder sb)
        {
            const float y0 = 2f;
            const float xStep = 0.02f, zStep = 0.02f;
            const float bucketSize = 0.02f;

            var inst = Object.Instantiate(asset);
            inst.name = "ProbeInstance";
            inst.transform.position = Vector3.zero;

            var meshCollider = inst.AddComponent<MeshCollider>();
            meshCollider.sharedMesh = inst.GetComponentInChildren<MeshFilter>().sharedMesh;

            // 范围按**实测包围盒**取（不写死）：两个模型的尺寸与朝向都不一样，写死会漏掉一半
            bool bounded = false;
            var bounds = new Bounds();
            foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
            {
                if (bounded) bounds.Encapsulate(r.bounds); else { bounds = r.bounds; bounded = true; }
            }
            const float pad = 0.01f;
            float xMin = bounds.min.x - pad, xMax = bounds.max.x + pad;
            float zMin = bounds.min.z - pad, zMax = bounds.max.z + pad;

            var buckets = new SortedDictionary<int, Bucket>();
            int hits = 0;

            for (float z = zMin; z <= zMax + 0.001f; z += zStep)
            {
                for (float x = xMin; x <= xMax + 0.001f; x += xStep)
                {
                    if (!Physics.Raycast(new Vector3(x, y0, z), Vector3.down, out var hit, y0 + 1f,
                            ~0, QueryTriggerInteraction.Ignore))
                    {
                        continue;
                    }

                    hits++;
                    int key = Mathf.RoundToInt(hit.point.y / bucketSize);
                    if (!buckets.TryGetValue(key, out var b)) b = new Bucket { X0 = x, X1 = x, Z0 = z, Z1 = z };
                    b.Count++;
                    b.X0 = Mathf.Min(b.X0, x); b.X1 = Mathf.Max(b.X1, x);
                    b.Z0 = Mathf.Min(b.Z0, z); b.Z1 = Mathf.Max(b.Z1, z);
                    buckets[key] = b;
                }
            }

            sb.AppendLine($"  -- 顶面高度分桶（网格 2cm；命中 {hits} 点）--");
            sb.AppendLine("     高度   点数   x 范围           z 范围");
            foreach (var kv in buckets)
            {
                if (kv.Value.Count < 4) continue;   // 4 点以下当噪声（细缝/边角）
                var b = kv.Value;
                sb.AppendLine($"     {kv.Key * bucketSize,5:0.00}  {b.Count,5}   " +
                              $"x[{b.X0,6:0.00},{b.X1,6:0.00}]  z[{b.Z0,6:0.00},{b.Z1,6:0.00}]");
            }

            GridMap(sb, y0, xMin, xMax, zMin, zMax);
            sb.AppendLine();
            Object.DestroyImmediate(inst);
        }

        /// <summary>
        /// **顶面高度网格图**（x 每 6cm、z 每 5cm 一格）：格子里写该格**顶面**的高度（cm），
        /// 打不到就是 <c>·</c>。行 = z（从 −Z 到 +Z），列 = x。
        ///
        /// 为什么还要这张图：分桶表只知道"0.44 高上有 30 个点"，**不知道那 30 个点在哪儿** ——
        /// 座椅、仪表台、风挡框在桶里全糊成一堆范围数。网格图里一眼就能看出
        /// "两片对称的 44"= 左右两张座垫，"横贯的 56"= 仪表台顶面，"贴边的 70"= 侧镜/风挡框。
        /// </summary>
        static void GridMap(StringBuilder sb, float y0, float xMin, float xMax, float zMin, float zMax)
        {
            const float gx = 0.06f, gz = 0.05f;

            sb.AppendLine("  -- 顶面高度网格图（行 = z，列 = x；数字 = 该格顶面高度 cm，· = 无命中）--");
            var head = new StringBuilder("        z\\x ");
            for (float x = xMin; x <= xMax + 0.001f; x += gx) head.Append($"{x * 100f,5:0}");
            sb.AppendLine(head.ToString());

            for (float z = zMin; z <= zMax + 0.001f; z += gz)
            {
                var row = new StringBuilder($"      {z * 100f,5:0} ");
                for (float x = xMin; x <= xMax + 0.001f; x += gx)
                {
                    row.Append(Physics.Raycast(new Vector3(x, y0, z), Vector3.down, out var hit, y0 + 1f,
                            ~0, QueryTriggerInteraction.Ignore)
                        ? $"{hit.point.y * 100f,5:0}"
                        : "    ·");
                }
                sb.AppendLine(row.ToString());
            }
        }

        struct Bucket
        {
            public int Count;
            public float X0, X1, Z0, Z1;
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
