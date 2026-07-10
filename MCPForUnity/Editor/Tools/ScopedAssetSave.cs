using System;
using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services.MutationTransactions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MCPForUnity.Editor.Tools
{
    [McpForUnityTool("save_scene_scoped", AutoRegister = true, Group = "core")]
    public static class SaveSceneScoped
    {
        public static object HandleCommand(JObject @params) => ScopedAssetSave.HandleScene(@params, false);
    }

    [McpForUnityTool("save_prefab_scoped", AutoRegister = true, Group = "core")]
    public static class SavePrefabScoped
    {
        public static object HandleCommand(JObject @params) => ScopedAssetSave.HandleAssets(@params, true, false);
    }

    [McpForUnityTool("save_assets_scoped", AutoRegister = true, Group = "core")]
    public static class SaveAssetsScoped
    {
        public static object HandleCommand(JObject @params) => ScopedAssetSave.HandleAssets(@params, false, false);
    }

    [McpForUnityTool("preview_asset_changes", AutoRegister = true, Group = "core")]
    public static class PreviewAssetChanges
    {
        public static object HandleCommand(JObject @params)
        {
            var p = new ToolParams(@params ?? new JObject());
            return p.GetRaw("scenePath") != null || p.GetRaw("sceneName") != null
                ? ScopedAssetSave.HandleScene(@params, true)
                : ScopedAssetSave.HandleAssets(@params, false, true);
        }
    }

    internal static class ScopedAssetSave
    {
        public static object HandleScene(JObject @params, bool preview)
        {
            var p = new ToolParams(@params ?? new JObject());
            string path = NormalizePath(p.Get("scenePath") ?? p.Get("path"));
            string name = p.Get("sceneName") ?? p.Get("name");
            Scene scene = FindLoadedScene(path, name);
            if (!scene.IsValid() || !scene.isLoaded)
                return Error("TARGET_NOT_FOUND", "The requested scene is not loaded. Provide scene_path or scene_name for a loaded scene.");
            if (string.IsNullOrEmpty(scene.path))
                return Error("SCENE_UNSAVED", "The requested scene has no asset path and cannot be saved scoped.");

            string[] paths = { NormalizePath(scene.path) };
            object[] dirtyObjects = DirtyObjects(paths);
            string[] dirtyPaths = DirtyRequestedPaths(paths);
            if (preview)
                return Preview(paths, dirtyObjects);

            DirtyScenePolicy policy = ParseDirtyScenePolicy(p.Get("dirtyScenePolicy"), DirtyScenePolicy.Allow);
            try
            {
                using (MutationTransaction transaction = MutationTransaction.BeginScene(scene, new MutationTransactionOptions
                {
                    Name = "MCP scoped scene save",
                    DirtyScenePolicy = policy,
                    AllowDirtyAssets = true
                }))
                {
                    IReadOnlyList<SerializedChange> changes = transaction.Commit(save: true);
                    return Saved(dirtyPaths, dirtyObjects, changes);
                }
            }
            catch (MutationTransactionException exception)
            {
                return Error(exception.Code, exception.Message, new { assets = paths, rolled_back = true });
            }
        }

        public static object HandleAssets(JObject @params, bool prefabOnly, bool preview)
        {
            var p = new ToolParams(@params ?? new JObject());
            string[] paths = ReadPaths(p, prefabOnly);
            if (paths.Length == 0)
                return Error("INVALID_PARAMS", prefabOnly
                    ? "Provide prefab_path for the prefab to save."
                    : "Provide a non-empty asset_paths list.");
            string invalid = paths.FirstOrDefault(path => !path.StartsWith("Assets/", StringComparison.Ordinal)
                || path.Contains("../") || AssetDatabase.IsValidFolder(path));
            if (invalid != null)
                return Error("INVALID_ASSET_PATH", $"'{invalid}' must identify an authored asset under Assets/.");
            if (!prefabOnly && paths.Any(path => path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)))
                return Error("INVALID_ASSET_PATH", "save_assets_scoped does not accept scene assets; use save_scene_scoped for a loaded scene.");
            string missing = paths.FirstOrDefault(path => AssetDatabase.LoadMainAssetAtPath(path) == null);
            if (missing != null)
                return Error("TARGET_NOT_FOUND", $"Asset '{missing}' was not found.");
            if (prefabOnly && paths.Any(path => !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)))
                return Error("INVALID_ASSET_PATH", "save_prefab_scoped accepts only a .prefab asset path.");

            object[] dirtyObjects = DirtyObjects(paths);
            string[] dirtyPaths = DirtyRequestedPaths(paths);
            if (preview)
                return Preview(paths, dirtyObjects);

            try
            {
                using (MutationTransaction transaction = MutationTransaction.BeginAssets(paths, new MutationTransactionOptions
                {
                    Name = "MCP scoped asset save",
                    AllowDirtyAssets = true
                }))
                {
                    IReadOnlyList<SerializedChange> changes = transaction.Commit(save: true);
                    return Saved(dirtyPaths, dirtyObjects, changes);
                }
            }
            catch (MutationTransactionException exception)
            {
                return Error(exception.Code, exception.Message, new { assets = paths, rolled_back = true });
            }
        }

        private static object Saved(string[] savedPaths, object[] dirtyObjects, IReadOnlyList<SerializedChange> changes) =>
            new SuccessResponse($"Scoped save completed for {savedPaths.Length} dirty asset(s).", new
            {
                assets_saved = savedPaths,
                objects_changed = dirtyObjects,
                properties_changed = changes,
                dirty_assets_left_unsaved = DirtyAssetPaths(savedPaths),
                rolled_back = false
            });

        private static object Preview(string[] paths, object[] dirtyObjects) =>
            new SuccessResponse($"Previewed {paths.Length} scoped asset(s) without saving.", new
            {
                assets_saved = Array.Empty<string>(),
                objects_changed = dirtyObjects,
                properties_changed = Array.Empty<object>(),
                dirty_assets_left_unsaved = DirtyAssetPaths(Array.Empty<string>()),
                requested_assets = paths,
                rolled_back = false,
                preview = true
            });

        private static object[] DirtyObjects(IEnumerable<string> paths)
        {
            var result = new List<object>();
            foreach (string path in paths)
            foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(path).Where(item => item != null && EditorUtility.IsDirty(item)))
                result.Add(new
                {
                    asset_path = path,
                    object_name = asset.name,
                    object_type = asset.GetType().FullName,
                    global_object_id = GlobalObjectId.GetGlobalObjectIdSlow(asset).ToString()
                });

            foreach (Scene scene in LoadedScenes().Where(scene => paths.Contains(NormalizePath(scene.path)) && scene.isDirty))
            {
                int countBeforeScene = result.Count;
                foreach (GameObject root in scene.GetRootGameObjects())
                foreach (Object item in SceneObjects(root).Where(EditorUtility.IsDirty))
                    result.Add(new
                    {
                        asset_path = NormalizePath(scene.path),
                        object_name = item.name,
                        object_type = item.GetType().FullName,
                        global_object_id = GlobalObjectId.GetGlobalObjectIdSlow(item).ToString()
                    });
                if (result.Count == countBeforeScene)
                    result.Add(new
                    {
                        asset_path = NormalizePath(scene.path),
                        object_name = scene.name,
                        object_type = "UnityEngine.SceneManagement.Scene",
                        global_object_id = (string)null
                    });
            }
            return result.ToArray();
        }

        private static IEnumerable<Object> SceneObjects(GameObject root)
        {
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
            {
                yield return transform.gameObject;
                foreach (Component component in transform.GetComponents<Component>())
                    if (component != null) yield return component;
            }
        }

        private static string[] DirtyRequestedPaths(IEnumerable<string> paths)
        {
            var requested = new HashSet<string>(paths.Select(NormalizePath), StringComparer.Ordinal);
            var dirty = new HashSet<string>(StringComparer.Ordinal);
            foreach (Scene scene in LoadedScenes())
                if (scene.isDirty && requested.Contains(NormalizePath(scene.path))) dirty.Add(NormalizePath(scene.path));
            foreach (string path in requested)
                if (AssetDatabase.LoadAllAssetsAtPath(path).Any(item => item != null && EditorUtility.IsDirty(item)))
                    dirty.Add(path);
            return dirty.OrderBy(path => path).ToArray();
        }

        private static string[] DirtyAssetPaths(IEnumerable<string> excluded)
        {
            var excludedSet = new HashSet<string>(excluded.Select(NormalizePath), StringComparer.Ordinal);
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (Scene scene in LoadedScenes())
                if (scene.isDirty && !string.IsNullOrEmpty(scene.path)) result.Add(NormalizePath(scene.path));
            foreach (Object asset in UnityEngine.Resources.FindObjectsOfTypeAll<Object>())
            {
                if (asset == null || !EditorUtility.IsDirty(asset)) continue;
                string path = NormalizePath(AssetDatabase.GetAssetPath(asset));
                if (!string.IsNullOrEmpty(path) && path.StartsWith("Assets/", StringComparison.Ordinal)) result.Add(path);
            }
            return result.Where(path => !excludedSet.Contains(path)).OrderBy(path => path).Take(200).ToArray();
        }

        private static Scene FindLoadedScene(string path, string name)
        {
            foreach (Scene scene in LoadedScenes())
            {
                if (!string.IsNullOrEmpty(path) && string.Equals(NormalizePath(scene.path), path, StringComparison.Ordinal)) return scene;
                if (!string.IsNullOrEmpty(name) && string.Equals(scene.name, name, StringComparison.Ordinal)) return scene;
            }
            return default;
        }

        private static IEnumerable<Scene> LoadedScenes()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++) yield return SceneManager.GetSceneAt(i);
        }

        private static string[] ReadPaths(ToolParams p, bool prefabOnly)
        {
            if (prefabOnly)
            {
                string path = NormalizePath(p.Get("prefabPath") ?? p.Get("path"));
                return string.IsNullOrEmpty(path) ? Array.Empty<string>() : new[] { path };
            }
            string[] paths = p.GetStringArray("assetPaths") ?? p.GetStringArray("paths");
            if (paths == null)
            {
                string path = p.Get("path");
                paths = string.IsNullOrWhiteSpace(path) ? Array.Empty<string>() : new[] { path };
            }
            return paths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(NormalizePath).Distinct(StringComparer.Ordinal).ToArray();
        }

        private static DirtyScenePolicy ParseDirtyScenePolicy(string value, DirtyScenePolicy fallback)
        {
            if (string.Equals(value, "reject", StringComparison.OrdinalIgnoreCase)) return DirtyScenePolicy.Reject;
            if (string.Equals(value, "preserve", StringComparison.OrdinalIgnoreCase)) return DirtyScenePolicy.Preserve;
            if (string.Equals(value, "allow", StringComparison.OrdinalIgnoreCase)) return DirtyScenePolicy.Allow;
            return fallback;
        }

        private static string NormalizePath(string path) => path?.Replace('\\', '/');

        private static ErrorResponse Error(string code, string message, object data = null) =>
            new ErrorResponse(code, new { message, data });
    }
}
