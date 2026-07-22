using System;
using System.Collections.Generic;
using System.IO;
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

            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return Error("PLAY_MODE_ACTIVE", "Scenes cannot be saved while the editor is playing. Stop Play Mode first.");

            // exclude: merge only ledgered/new blocks into the on-disk file, suppressing ambient
            // in-memory drift. include (default): legacy whole-scene save. reject: fail if any
            // unscoped drift would have been suppressed. include stays the default until every
            // scene-mutating tool records into SceneMutationLedger — today only ComponentOps,
            // ManageComponents, and the GameObjects tools do, so exclude would silently discard
            // intentional edits from uninstrumented tools (cameras, physics, VFX, UI, ...).
            string unscoped = (p.Get("unscopedChanges") ?? "include").Trim().ToLowerInvariant();
            if (unscoped != "exclude" && unscoped != "include" && unscoped != "reject")
                return Error("INVALID_PARAMS", "unscoped_changes must be one of: exclude, include, reject.");
            if (unscoped != "include")
                return MergeSave(scene, rejectDrift: unscoped == "reject", dirtyObjects);

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

        /// <summary>
        /// Serializes the scene to a temp copy (in-memory truth), then writes a merged scene file
        /// where only new blocks and blocks the <see cref="SceneMutationLedger"/> marks as
        /// intentionally mutated come from that serialization; everything else keeps its on-disk
        /// bytes. This is what makes the save "scoped": ambient drift from [ExecuteAlways]
        /// previews or layout-driven RectTransforms is reported, not baked in.
        /// </summary>
        private static object MergeSave(Scene scene, bool rejectDrift, object[] dirtyObjects)
        {
            string scenePath = NormalizePath(scene.path);
            (IReadOnlyCollection<string> touched, IReadOnlyCollection<string> deleted) =
                SceneMutationLedger.SnapshotForScene(scenePath);
            string sceneGuid = AssetDatabase.AssetPathToGUID(scenePath);
            HashSet<long> scopedIds = LedgerIdsToSceneFileIds(touched, sceneGuid);
            HashSet<long> deletedIds = LedgerIdsToSceneFileIds(deleted, sceneGuid);

            string tempPath = "Temp/mcp_scoped_scene_save.unity";
            string tempText;
            SceneMutationLedger.SuppressSceneSavedClear = true;
            try
            {
                if (!EditorSceneManager.SaveScene(scene, tempPath, saveAsCopy: true))
                    return Error("SAVE_FAILED", "Unity could not serialize the scene to a temporary copy.");
                tempText = File.ReadAllText(tempPath);
            }
            finally
            {
                SceneMutationLedger.SuppressSceneSavedClear = false;
                try { File.Delete(tempPath); } catch { /* temp cleanup only */ }
            }

            string diskText = File.ReadAllText(scenePath);
            SceneYamlMerge.MergeResult merge;
            try
            {
                merge = SceneYamlMerge.Merge(diskText, tempText, scopedIds, deletedIds);
            }
            // ArgumentException covers duplicate fileIDs (Merge's ToDictionary) in a corrupt scene.
            catch (Exception exception) when (exception is FormatException || exception is ArgumentException)
            {
                return Error("MERGE_FAILED", $"Scene YAML could not be merged: {exception.Message}. " +
                    "Retry with unscoped_changes=include for a legacy whole-scene save.");
            }

            object drift = DriftReport(merge);
            if (rejectDrift && (merge.BlocksDriftSuppressed > 0 || merge.UnscopedDeletionsKept.Count > 0))
                return Error("UNSCOPED_CHANGES", "The scene holds unscoped in-memory changes and unscoped_changes=reject was requested.", drift);

            bool changed = !string.Equals(merge.MergedText, NormalizeNewlines(diskText), StringComparison.Ordinal);
            if (changed)
            {
                File.WriteAllText(scenePath, merge.MergedText);
                AssetDatabase.ImportAsset(scenePath);
            }
            SceneMutationLedger.Clear(scenePath);

            string driftHint = merge.BlocksDriftSuppressed > 0 || merge.UnscopedDeletionsKept.Count > 0
                ? " Unscoped in-memory changes were kept out of the file (ambient drift, Inspector or execute_code edits); if some were intentional, save via the editor or retry with unscoped_changes=include."
                : string.Empty;
            return new SuccessResponse(
                (changed
                    ? $"Merged scoped save: {merge.BlocksNew} new, {merge.BlocksScoped} scoped, {merge.BlocksDeleted} deleted block(s) written; {merge.BlocksDriftSuppressed} drifted block(s) left untouched."
                    : "No scoped changes to write; the on-disk scene already matches.") + driftHint,
                new
                {
                    assets_saved = changed ? new[] { scenePath } : Array.Empty<string>(),
                    save_mode = "merge_scoped",
                    blocks = new
                    {
                        total = merge.BlocksTotal,
                        @new = merge.BlocksNew,
                        scoped = merge.BlocksScoped,
                        deleted = merge.BlocksDeleted,
                        drift_suppressed = merge.BlocksDriftSuppressed,
                    },
                    unscoped_drift = drift,
                    scene_still_dirty_in_memory = scene.isDirty,
                    dirty_objects = SummarizeDirty(dirtyObjects),
                    dirty_assets_left_unsaved = DirtyAssetPaths(changed ? new[] { scenePath } : Array.Empty<string>()),
                    rolled_back = false,
                });
        }

        private static object DriftReport(SceneYamlMerge.MergeResult merge) => new
        {
            drift_suppressed_block_count = merge.BlocksDriftSuppressed,
            drift_suppressed_file_ids = merge.DriftSuppressedIds.Take(20).Select(id => id.ToString()).ToArray(),
            unscoped_deletions_kept = merge.UnscopedDeletionsKept.Select(id => id.ToString()).ToArray(),
            splice_notes = merge.Warnings.Take(20).ToArray(),
        };

        /// <summary>Maps ledger GlobalObjectId strings to scene-file fileIDs (prefab-instance members map to their PrefabInstance block).</summary>
        private static HashSet<long> LedgerIdsToSceneFileIds(IEnumerable<string> ledgerIds, string sceneGuid)
        {
            var fileIds = new HashSet<long>();
            foreach (string ledgerId in ledgerIds)
            {
                if (!GlobalObjectId.TryParse(ledgerId, out GlobalObjectId id))
                    continue;
                if (id.identifierType != 2 || !string.Equals(id.assetGUID.ToString(), sceneGuid, StringComparison.Ordinal))
                    continue;
                fileIds.Add(unchecked((long)(id.targetPrefabId != 0 ? id.targetPrefabId : id.targetObjectId)));
            }
            return fileIds;
        }

        private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n");

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

        private sealed class DirtyEntry
        {
            public string asset_path;
            public string object_name;
            public string object_type;
            public string global_object_id;
        }

        private const int DirtyObjectListCap = 25;

        /// <summary>
        /// Dirty-object payload that stays small when hundreds of objects have ambient in-memory
        /// drift: a total, a per-type histogram, and a capped sample instead of the raw list.
        /// </summary>
        private static object SummarizeDirty(object[] dirtyObjects)
        {
            DirtyEntry[] entries = dirtyObjects.OfType<DirtyEntry>().ToArray();
            return new
            {
                total = dirtyObjects.Length,
                by_type = entries
                    .GroupBy(entry => entry.object_type)
                    .OrderByDescending(group => group.Count())
                    .ToDictionary(group => group.Key, group => group.Count()),
                sample = dirtyObjects.Take(DirtyObjectListCap).ToArray(),
                sample_truncated = dirtyObjects.Length > DirtyObjectListCap,
            };
        }

        private static object Saved(string[] savedPaths, object[] dirtyObjects, IReadOnlyList<SerializedChange> changes) =>
            new SuccessResponse($"Scoped save completed for {savedPaths.Length} dirty asset(s).", new
            {
                assets_saved = savedPaths,
                objects_changed = SummarizeDirty(dirtyObjects),
                properties_changed = changes,
                dirty_assets_left_unsaved = DirtyAssetPaths(savedPaths),
                rolled_back = false
            });

        private static object Preview(string[] paths, object[] dirtyObjects) =>
            new SuccessResponse($"Previewed {paths.Length} scoped asset(s) without saving.", new
            {
                assets_saved = Array.Empty<string>(),
                objects_changed = SummarizeDirty(dirtyObjects),
                properties_changed = Array.Empty<object>(),
                dirty_assets_left_unsaved = DirtyAssetPaths(Array.Empty<string>()),
                requested_assets = paths,
                rolled_back = false,
                preview = true
            });

        private static object[] DirtyObjects(IEnumerable<string> paths)
        {
            var result = new List<object>();
            // Loaded scenes are handled below; LoadAllAssetsAtPath on a scene
            // path raises internal ReadObjectThreaded errors on newer editors.
            foreach (string path in paths.Where(path => !path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)))
            foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(path).Where(item => item != null && EditorUtility.IsDirty(item)))
                result.Add(new DirtyEntry
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
                    result.Add(new DirtyEntry
                    {
                        asset_path = NormalizePath(scene.path),
                        object_name = item.name,
                        object_type = item.GetType().FullName,
                        global_object_id = GlobalObjectId.GetGlobalObjectIdSlow(item).ToString()
                    });
                if (result.Count == countBeforeScene)
                    result.Add(new DirtyEntry
                    {
                        asset_path = NormalizePath(scene.path),
                        object_name = scene.name,
                        object_type = "UnityEngine.SceneManagement.Scene",
                        global_object_id = null
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
            foreach (string path in requested.Where(path => !path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)))
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
