using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
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
