using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;
using VRC.SDKBase;

#if UNITY_EDITOR
using UnityEditor;
using UnityEngine.Networking;
#endif

namespace zhuozhi.MA2BTPro
{
    [AddComponentMenu("MA2BT Pro/MA2BT Pro")]
    [DisallowMultipleComponent]
    public class MAToBlendTreePro : MonoBehaviour, IEditorOnly
    {
        [Tooltip("紧凑模式：只生成存在动画或边界保护需要的阈值，减少空状态。关闭后参数树和嵌套树都会生成完整整数阈值表。")]
        public bool compactMode = true;

        [Tooltip("多状态图层：转换包含多个条件状态的图层。关闭时会保留多状态图层。")]
        public bool convertMultiState = true;

        [Tooltip("合并相同混合树 / 动画：当多个图层的多状态参数结构完全一致时，合并成一个嵌套混合树，并把相同状态的动画曲线合并到同一个动画里。关闭后会按图层分别生成嵌套树，便于排查转换问题。")]
        public bool mergeIdenticalBlendTreesAndAnimations = true;

        [Tooltip("扫描所有图层（不建议开启）：扫描所有 FX 图层，而不仅仅是 MA 生成的图层。外部图层可能包含复杂 BlendTree、特殊条件或行为语义，开启后有更高概率出现转换不等价。")]
        public bool scanAllLayers = false;

        [Tooltip("图层名前缀排除列表。图层名以前缀列表中任意内容开头时，MA2BT Pro 会保留该图层，不进行转换。可以在这里添加、删除或修改前缀。")]
        public List<string> excludedLayerPrefixes = new List<string>
        {
            "lilycalInventory",
            "AutoDresser"
        };

        [Tooltip("状态名前缀排除列表。任意状态名以前缀列表中任意内容开头时，MA2BT Pro 会保留整个图层，不进行转换。可以在这里添加、删除或修改前缀。")]
        public List<string> excludedStatePrefixes = new List<string>
        {
            "Root",
            "root"
        };

        [Tooltip("参数名前缀排除列表。参数名以前缀列表中任意内容开头时，MA2BT Pro 会保留使用该参数的图层，不转换该参数，也不会进行 Bool / Int 到 Float 的迁移。可以在这里添加、删除或修改前缀。")]
        public List<string> excludedParameterPrefixes = new List<string>
        {
        };

#if UNITY_EDITOR
        public static bool IsNDMFCompatible(out string message)
        {
            var missingApis = new List<string>();

            var virtualBlendTreeType = FindLoadedType("nadena.dev.ndmf.animator.VirtualBlendTree");
            if (virtualBlendTreeType == null)
            {
                missingApis.Add("VirtualBlendTree");
            }
            else
            {
                var normalizedBlendValuesProperty = virtualBlendTreeType.GetProperty(
                    "NormalizedBlendValues",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);

                if (normalizedBlendValuesProperty == null ||
                    !normalizedBlendValuesProperty.CanRead ||
                    !normalizedBlendValuesProperty.CanWrite)
                {
                    missingApis.Add("VirtualBlendTree.NormalizedBlendValues");
                }
            }

            if (missingApis.Count == 0)
            {
                message = null;
                return true;
            }

            message = "当前 NDMF / Modular Avatar 版本过旧，缺少 MA2BT Pro 需要的 API："
                + string.Join("、", missingApis)
                + "。请更新 NDMF 或 Modular Avatar 后再使用。为避免生成错误结果，MA2BT Pro 不会执行任何优化，并会在构建时直接跳过。";
            return false;
        }

        static Type FindLoadedType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetType(fullName, false);
                    if (type != null)
                        return type;
                }
                catch
                {
                    // 有些编辑器扩展的程序集在反射时可能抛异常，跳过就好。
                }
            }

            return null;
        }
#endif
    }

#if UNITY_EDITOR
    [CustomEditor(typeof(MAToBlendTreePro))]
    public class MAToBlendTreeProEditor : Editor
    {
        static readonly Color HeaderColor = new Color(0.55f, 0.2f, 0.85f);
        static readonly Color ErrorHeaderColor = new Color(0.75f, 0.12f, 0.12f);
        static readonly Color VersionBoxColor = new Color(0.55f, 0.2f, 0.85f, 0.10f);
        static readonly Color VersionTextColor = new Color(0.42f, 0.22f, 0.58f);
        static readonly Color UpdateTextColor = new Color(0.72f, 0.28f, 0.95f);

        const string VERSION = "1.2.9";
        const string BILI_VIDEO_URL = "https://www.bilibili.com/video/BV1xHGp66ENJ";
        const string BILI_API = "https://api.bilibili.com/x/web-interface/view?bvid=BV1xHGp66ENJ";

        const string LAST_CHECK_KEY = "MA2BTPro_LastUpdateCheckTime";
        const string CACHED_LATEST_VERSION_KEY = "MA2BTPro_CachedLatestVersion";
        const int CHECK_INTERVAL_HOURS = 1;

        static bool updateCheckStarted = false;
        static bool hasUpdate = false;
        static string latestVersion = "";

        public override void OnInspectorGUI()
        {
            if (!updateCheckStarted)
            {
                updateCheckStarted = true;

                LoadCachedUpdateResult();

                _ = CheckUpdateAsync();
            }

            serializedObject.Update();

            bool ndmfCompatible = MAToBlendTreePro.IsNDMFCompatible(out var ndmfCompatibilityMessage);

            EditorGUILayout.Space(4);
            var headerRect = EditorGUILayout.GetControlRect(false, 22);
            var headerColor = ndmfCompatible ? HeaderColor : ErrorHeaderColor;
            EditorGUI.DrawRect(headerRect, headerColor);

            var headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white },
                fontSize = 13
            };

            string title;
            if (!ndmfCompatible)
            {
                title = $"MA2BT Pro  v{VERSION}    NDMF / Modular Avatar 版本过旧";
            }
            else if (hasUpdate && !string.IsNullOrEmpty(latestVersion))
            {
                title = $"MA2BT Pro  v{VERSION}    发现新版本 v{latestVersion}";
            }
            else
            {
                title = $"MA2BT Pro  v{VERSION}";
            }

            EditorGUI.LabelField(headerRect, title, headerStyle);

            if (!ndmfCompatible)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(ndmfCompatibilityMessage, MessageType.Error);

                EditorGUILayout.Space(4);
            }

            if (hasUpdate)
            {
                EditorGUILayout.Space(4);

                if (GUILayout.Button("打开更新页面", GUILayout.Height(22)))
                {
                    Application.OpenURL(BILI_VIDEO_URL);
                }

                EditorGUILayout.Space(4);
            }
            else
            {
                EditorGUILayout.Space(6);
            }

            DrawToggle("compactMode", "紧凑模式", "只生成存在动画或边界保护需要的阈值，减少空状态。关闭后参数树和嵌套树都会生成完整整数阈值表。");
            DrawToggle("convertMultiState", "多状态图层", "转换包含多个条件状态的图层。关闭时会保留多状态图层。");
            DrawToggle("mergeIdenticalBlendTreesAndAnimations", "合并相同混合树 / 动画", "当多个图层的多状态参数结构完全一致时，合并成一个嵌套混合树，并把相同状态的动画曲线合并到同一个动画里。关闭后会按图层分别生成嵌套树，便于排查转换问题。");
            DrawToggle("scanAllLayers", "扫描所有图层（不建议开启）", "扫描所有 FX 图层，而不仅仅是 MA 生成的图层。外部图层可能包含复杂 BlendTree、特殊条件或行为语义，开启后有更高概率出现转换不等价。");

            EditorGUILayout.Space(8);

            EditorGUILayout.LabelField("排除项", EditorStyles.boldLabel);
            DrawPrefixList("excludedLayerPrefixes", "图层名前缀排除", "图层名前缀排除列表。图层名以前缀列表中任意内容开头时，MA2BT Pro 会保留该图层，不进行转换。可以在这里添加、删除或修改前缀。");
            DrawPrefixList("excludedStatePrefixes", "状态名前缀排除", "状态名前缀排除列表。任意状态名以前缀列表中任意内容开头时，MA2BT Pro 会保留整个图层，不进行转换。可以在这里添加、删除或修改前缀。");
            DrawPrefixList("excludedParameterPrefixes", "参数名前缀排除", "参数名前缀排除列表。参数名以前缀列表中任意内容开头时，MA2BT Pro 会保留使用该参数的图层，不转换该参数，也不会进行 Bool / Int 到 Float 的迁移。可以在这里添加、删除或修改前缀。");

            EditorGUILayout.Space(4);

            var footerStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
                normal = { textColor = new Color(0.5f, 0.5f, 0.5f) }
            };

            EditorGUILayout.LabelField("by: 浊鸷", footerStyle);
            EditorGUILayout.LabelField("MA2BT Pro反馈群: 798072555", footerStyle);
            EditorGUILayout.LabelField("MA2BT by: PuddingKC", footerStyle);

            serializedObject.ApplyModifiedProperties();
        }

        static async Task CheckUpdateAsync()
        {
            try
            {
                LoadCachedUpdateResult();

                string cachedRemoteVersion = EditorPrefs.GetString(CACHED_LATEST_VERSION_KEY, "");
                string lastCheck = EditorPrefs.GetString(LAST_CHECK_KEY, "");

                if (!string.IsNullOrEmpty(cachedRemoteVersion) && DateTime.TryParse(lastCheck, out var lastTime))
                {
                    if ((DateTime.Now - lastTime).TotalHours < CHECK_INTERVAL_HOURS)
                        return;
                }

                // 记录请求时间，避免 Inspector 反复打开时因为网络失败而频繁请求。
                EditorPrefs.SetString(LAST_CHECK_KEY, DateTime.Now.ToString());

                using var request = UnityWebRequest.Get(BILI_API);
                request.timeout = 10;

                var operation = request.SendWebRequest();
                while (!operation.isDone)
                    await Task.Yield();

                if (request.result != UnityWebRequest.Result.Success)
                    return;

                var response = JsonUtility.FromJson<BiliVideoResponse>(request.downloadHandler.text);
                string desc = response?.data?.desc;

                if (string.IsNullOrEmpty(desc))
                    return;

                string remoteVersion = ExtractLatestVersion(desc);
                if (string.IsNullOrEmpty(remoteVersion))
                    return;

                EditorPrefs.SetString(CACHED_LATEST_VERSION_KEY, remoteVersion);
                latestVersion = remoteVersion;
                hasUpdate = IsNewerVersion(remoteVersion, VERSION);
            }
            catch
            {
                // 静默失败
            }
        }

        static void LoadCachedUpdateResult()
        {
            latestVersion = EditorPrefs.GetString(CACHED_LATEST_VERSION_KEY, "");
            hasUpdate = !string.IsNullOrEmpty(latestVersion) && IsNewerVersion(latestVersion, VERSION);
        }

        static string ExtractLatestVersion(string text)
        {
            var match = Regex.Match(text, @"最新版本\s*[:：]\s*([0-9]+\.[0-9]+\.[0-9]+)");
            return match.Success ? match.Groups[1].Value : null;
        }

        static bool IsNewerVersion(string remoteVersion, string currentVersion)
        {
            try { return new Version(remoteVersion) > new Version(currentVersion); }
            catch { return false; }
        }

        void DrawToggle(string propName, string label, string tooltip)
        {
            var prop = serializedObject.FindProperty(propName);
            if (prop == null) return;
            prop.boolValue = EditorGUILayout.ToggleLeft(new GUIContent(label, tooltip), prop.boolValue);
        }

        void DrawPrefixList(string propName, string label, string tooltip)
        {
            var prop = serializedObject.FindProperty(propName);
            if (prop == null) return;
            EditorGUILayout.PropertyField(prop, new GUIContent(label, tooltip), true);
        }

        [Serializable] class BiliVideoResponse { public int code; public string message; public BiliVideoData data; }
        [Serializable] class BiliVideoData { public string desc; }
    }
#endif
}