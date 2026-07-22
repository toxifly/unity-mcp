using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using MCPForUnity.Runtime.Helpers;
using Object = UnityEngine.Object;

namespace MCPForUnity.Editor.Services.MutationTransactions
{
    public enum DirtyScenePolicy
    {
        Reject,
        Preserve,
        Allow
    }

    public sealed class MutationTransactionOptions
    {
        public string Name { get; set; } = "MCP scoped mutation";
        public DirtyScenePolicy DirtyScenePolicy { get; set; } = DirtyScenePolicy.Reject;
        public bool AllowDirtyAssets { get; set; }
        public IReadOnlyList<string> AdditionalAssetPaths { get; set; } = Array.Empty<string>();
    }

    public sealed class MutationTransactionException : Exception
    {
        public string Code { get; }

        public MutationTransactionException(string code, string message, Exception inner = null)
            : base(message, inner)
        {
            Code = code;
        }
    }

    public sealed class SerializedChange
    {
        public string Kind { get; internal set; }
        public string ObjectId { get; internal set; }
        public string ObjectPath { get; internal set; }
        public string AssetPath { get; internal set; }
        public string ComponentType { get; internal set; }
        public string Property { get; internal set; }
        public string Before { get; internal set; }
        public string After { get; internal set; }
    }

    /// <summary>
    /// Shared safety boundary for authored scene and asset mutations. Callers perform all
    /// edits with Unity's Undo-aware APIs, inspect Changes, then commit or roll back.
    /// </summary>
    public sealed class MutationTransaction : IDisposable
    {
        private static readonly MethodInfo ClearSceneDirtiness = typeof(EditorSceneManager).GetMethod(
            "ClearSceneDirtiness",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(Scene) },
            null);

        private static readonly HashSet<string> IgnoredProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "m_ObjectHideFlags"
        };

        private readonly MutationTransactionOptions options;
        private readonly List<Object> targets;
        private readonly List<Scene> targetScenes;
        private readonly List<string> targetAssetPaths;
        private readonly List<string> initiallyMissingAssetFolders;
        private readonly Dictionary<string, byte[]> assetBytes;
        private readonly Dictionary<string, string> initialAssetGuids;
        private readonly Dictionary<string, List<AssetObjectState>> initialDirtyAssetContents;
        private readonly Dictionary<string, Fingerprint> before;
        private readonly SceneSetup[] sceneSetup;
        private readonly Scene activeScene;
        private readonly string prefabStageAssetPath;
        private readonly Dictionary<string, bool> initialDirtyScenes;
        private readonly Dictionary<string, bool> initialDirtyAssets;
        private readonly SceneMutationLedger.Snapshot ledgerSnapshot;
        private readonly int undoGroup;
        private bool finished;

        private MutationTransaction(
            IEnumerable<Object> requestedTargets,
            MutationTransactionOptions requestedOptions,
            IEnumerable<Scene> requestedScenes = null)
        {
            options = requestedOptions ?? new MutationTransactionOptions();
            targets = requestedTargets?.Where(item => item != null).Distinct().ToList()
                ?? new List<Object>();
            targetScenes = ResolveTargetScenes(targets)
                .Concat(requestedScenes ?? Array.Empty<Scene>())
                .Where(scene => scene.IsValid() && scene.isLoaded)
                .GroupBy(SceneKey)
                .Select(group => group.First())
                .ToList();
            if (targets.Count == 0 && targetScenes.Count == 0
                && !(options.AdditionalAssetPaths?.Any() ?? false))
                throw new MutationTransactionException("TARGET_NOT_FOUND", "A mutation transaction requires at least one resolved target.");

            sceneSetup = EditorSceneManager.GetSceneManagerSetup();
            activeScene = SceneManager.GetActiveScene();
            prefabStageAssetPath = PrefabStageUtility.GetCurrentPrefabStage()?.assetPath;
            targetAssetPaths = ResolveTargetAssetPaths(targets, targetScenes, options.AdditionalAssetPaths);
            initiallyMissingAssetFolders = SnapshotMissingParentFolders(targetAssetPaths);
            initialDirtyScenes = targetScenes.ToDictionary(SceneKey, scene => scene.isDirty, StringComparer.Ordinal);
            initialDirtyAssets = targetAssetPaths
                .Where(path => !path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(
                    path => path,
                    path => AssetDatabase.LoadAllAssetsAtPath(path).Any(asset => asset != null && EditorUtility.IsDirty(asset)),
                    StringComparer.Ordinal);
            PreflightDirtyScenes();
            PreflightDirtyAssets();

            assetBytes = SnapshotAssetBytes(targetAssetPaths);
            initialAssetGuids = SnapshotAssetGuids(targetAssetPaths);
            initialDirtyAssetContents = SnapshotDirtyAssetContents(initialDirtyAssets);
            before = CaptureFingerprints(targets, targetScenes, targetAssetPaths);
            ledgerSnapshot = SceneMutationLedger.CaptureSnapshot();

            Undo.IncrementCurrentGroup();
            undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(options.Name);
            Object[] undoTargets = EnumerateSnapshotObjects(targets, targetScenes, targetAssetPaths).ToArray();
            if (undoTargets.Length > 0)
                Undo.RegisterCompleteObjectUndo(undoTargets, options.Name);
        }

        public static MutationTransaction Begin(
            IEnumerable<Object> targets,
            MutationTransactionOptions options = null)
        {
            return new MutationTransaction(targets, options);
        }

        public static MutationTransaction BeginScene(
            Scene scene,
            MutationTransactionOptions options = null)
        {
            return new MutationTransaction(Array.Empty<Object>(), options, new[] { scene });
        }

        public static MutationTransaction BeginAssets(
            IEnumerable<string> assetPaths,
            MutationTransactionOptions options = null)
        {
            options = options ?? new MutationTransactionOptions();
            options.AdditionalAssetPaths = assetPaths?.ToArray() ?? Array.Empty<string>();
            return new MutationTransaction(Array.Empty<Object>(), options);
        }

        public IReadOnlyList<string> TargetGlobalObjectIds =>
            targets.Select(ObjectIdentity).ToArray();

        public IReadOnlyList<string> TargetAssetPaths => targetAssetPaths.AsReadOnly();

        public IReadOnlyList<SerializedChange> Changes
        {
            get
            {
                EnsureOpen();
                Dictionary<string, Fingerprint> after = CaptureFingerprints(targets, targetScenes, targetAssetPaths);
                return Diff(before, after);
            }
        }

        /// <summary>Saves only the scenes and assets inferred from the declared targets.</summary>
        public IReadOnlyList<SerializedChange> Commit(bool save = true)
        {
            return Commit(null, save);
        }

        /// <summary>
        /// Validates the preview immediately before saving. A rejected or save-normalized
        /// change set is rolled back with the stable UNEXPECTED_SERIALIZED_CHANGES code.
        /// </summary>
        public IReadOnlyList<SerializedChange> Commit(
            Func<IReadOnlyList<SerializedChange>, bool> validator,
            bool save = true)
        {
            EnsureOpen();
            IReadOnlyList<SerializedChange> changes = Changes;
            try
            {
                if (validator != null && !validator(changes))
                {
                    RollbackInternal();
                    throw new MutationTransactionException(
                        "UNEXPECTED_SERIALIZED_CHANGES",
                        "The mutation change set was rejected and rolled back before saving.");
                }
                if (save)
                {
                    if (options.DirtyScenePolicy == DirtyScenePolicy.Preserve && initialDirtyScenes.Values.Any(value => value))
                        throw new MutationTransactionException(
                            "SCENE_ALREADY_DIRTY",
                            "A preserved pre-dirty scene cannot be saved by this transaction.");
                    Dictionary<int, string> identitiesBeforeSave = CaptureObjectIdentities();
                    SaveTargetsOnly();
                    IReadOnlyDictionary<string, string> stabilizedIdentities =
                        CaptureStabilizedIdentityAliases(identitiesBeforeSave);
                    IReadOnlyList<SerializedChange> savedChanges = Changes;
                    if (!SameChanges(changes, savedChanges, stabilizedIdentities))
                    {
                        RollbackInternal();
                        throw new MutationTransactionException(
                            "UNEXPECTED_SERIALIZED_CHANGES",
                            "Saving introduced additional serialized changes; the transaction was rolled back.");
                    }
                }
                RestoreUntouchedCleanScenes(changes);
                RestoreEditorSetup();
                Undo.CollapseUndoOperations(undoGroup);
                finished = true;
                return changes;
            }
            catch (MutationTransactionException)
            {
                if (!finished)
                    RollbackInternal();
                throw;
            }
            catch (Exception exception)
            {
                try
                {
                    RollbackInternal();
                }
                catch (Exception rollbackException)
                {
                    throw new MutationTransactionException(
                        "ROLLBACK_FAILED",
                        $"Saving failed and rollback also failed: {rollbackException.Message}",
                        exception);
                }
                throw new MutationTransactionException("SAVE_FAILED", $"Scoped save failed and was rolled back: {exception.Message}", exception);
            }
        }

        public IReadOnlyList<SerializedChange> Rollback()
        {
            EnsureOpen();
            IReadOnlyList<SerializedChange> changes = Changes;
            RollbackInternal();
            return changes;
        }

        public void Dispose()
        {
            if (!finished)
                RollbackInternal();
        }

        private void PreflightDirtyScenes()
        {
            if (options.DirtyScenePolicy != DirtyScenePolicy.Reject)
                return;
            Scene dirty = targetScenes.FirstOrDefault(scene => scene.isDirty);
            if (dirty.IsValid())
                throw new MutationTransactionException(
                    "SCENE_ALREADY_DIRTY",
                    $"Target scene '{SceneKey(dirty)}' already has unsaved changes. Save it or choose an explicit dirty-scene policy.");
        }

        private void PreflightDirtyAssets()
        {
            if (options.AllowDirtyAssets)
                return;
            string dirtyPath = initialDirtyAssets.FirstOrDefault(pair => pair.Value).Key;
            if (!string.IsNullOrEmpty(dirtyPath))
                throw new MutationTransactionException(
                    "ASSET_ALREADY_DIRTY",
                    $"Target asset '{dirtyPath}' already has unsaved changes and cannot be safely included.");
        }

        private void SaveTargetsOnly()
        {
            foreach (Scene scene in targetScenes)
            {
                if (!scene.isDirty)
                    continue;
                if (string.IsNullOrEmpty(scene.path))
                    throw new MutationTransactionException("SCENE_UNSAVED", "A target scene has no asset path and cannot be saved scoped.");
                if (!EditorSceneManager.SaveScene(scene))
                    throw new IOException($"Unity did not save scene '{scene.path}'.");
            }

            foreach (string path in targetAssetPaths.Where(path => !path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (Object dirtyAsset in AssetDatabase.LoadAllAssetsAtPath(path)
                    .Where(asset => asset != null && EditorUtility.IsDirty(asset)))
                    AssetDatabase.SaveAssetIfDirty(dirtyAsset);
            }
        }

        private void RollbackInternal()
        {
            if (finished)
                return;

            Exception failure = null;
            try
            {
                // Restore before RestoreEditorSetup: a scene reloaded from disk during setup
                // restore fires sceneOpened, which must clear the restored entries, not stale ones.
                SceneMutationLedger.RestoreSnapshot(ledgerSnapshot);
                SceneMutationLedger.SuppressUndoRedoClear = true;
                try
                {
                    Undo.RevertAllDownToGroup(undoGroup);
                }
                finally
                {
                    SceneMutationLedger.SuppressUndoRedoClear = false;
                }
                RestoreAssetMoves();
                IReadOnlyCollection<string> importedAssetPaths = RestoreAssetBytes();
                RestoreDirtyAssetContents(importedAssetPaths);
                RestoreMissingAssetFolders();
                RestoreDirtyStates();
                RestoreEditorSetup();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                finished = true;
            }

            if (failure != null)
                throw new MutationTransactionException("ROLLBACK_FAILED", $"Mutation rollback failed: {failure.Message}", failure);
        }

        private void RestoreUntouchedCleanScenes(IReadOnlyList<SerializedChange> changes)
        {
            // Asset-only commits (e.g. SaveAsPrefabAsset without linking) can flag a
            // target scene dirty even though none of its objects changed. Scene-side
            // fingerprints carry an empty AssetPath or the scene's own path.
            bool anySceneSideChange = changes.Any(change =>
                string.IsNullOrEmpty(change.AssetPath)
                || change.AssetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase));
            if (anySceneSideChange)
                return;
            foreach (Scene scene in targetScenes)
            {
                if (!scene.IsValid() || !scene.isLoaded || !scene.isDirty)
                    continue;
                if (initialDirtyScenes.TryGetValue(SceneKey(scene), out bool wasDirty) && wasDirty)
                    continue;
                if (ClearSceneDirtiness != null)
                    ClearSceneDirtiness.Invoke(null, new object[] { scene });
            }
        }

        private void RestoreDirtyStates()
        {
            foreach (Scene scene in targetScenes)
            {
                if (!scene.IsValid() || !scene.isLoaded)
                    continue;
                bool wasDirty = initialDirtyScenes.TryGetValue(SceneKey(scene), out bool dirty) && dirty;
                if (wasDirty)
                    EditorSceneManager.MarkSceneDirty(scene);
                else if (scene.isDirty)
                {
                    if (ClearSceneDirtiness == null)
                        throw new MissingMethodException(
                            typeof(EditorSceneManager).FullName,
                            "ClearSceneDirtiness");
                    ClearSceneDirtiness.Invoke(null, new object[] { scene });
                    if (scene.isDirty)
                        throw new InvalidOperationException(
                            $"Unity did not restore scene '{SceneKey(scene)}' to its initial clean state.");
                }
            }

            foreach (KeyValuePair<string, bool> pair in initialDirtyAssets)
            foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(pair.Key).Where(asset => asset != null))
            {
                if (pair.Value)
                    EditorUtility.SetDirty(asset);
                else
                    EditorUtility.ClearDirty(asset);
            }
        }

        private IReadOnlyCollection<string> RestoreAssetBytes()
        {
            var changedPaths = new List<string>();
            foreach (KeyValuePair<string, byte[]> pair in assetBytes)
            {
                string fullPath = FullProjectPath(pair.Key);
                if (pair.Value == null)
                {
                    if (File.Exists(fullPath))
                        AssetDatabase.DeleteAsset(pair.Key);
                }
                else if (!File.Exists(fullPath) || !File.ReadAllBytes(fullPath).SequenceEqual(pair.Value))
                {
                    File.WriteAllBytes(fullPath, pair.Value);
                    changedPaths.Add(pair.Key);
                }
            }
            foreach (string path in changedPaths)
                AssetDatabase.ImportAsset(
                    path,
                    ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            return changedPaths;
        }

        private void RestoreAssetMoves()
        {
            var declaredPaths = new HashSet<string>(targetAssetPaths, StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> pair in initialAssetGuids)
            {
                string originalPath = pair.Key;
                string originalGuid = pair.Value;
                if (string.IsNullOrEmpty(originalGuid))
                    continue;

                string currentPath = AssetDatabase.GUIDToAssetPath(originalGuid);
                if (string.IsNullOrEmpty(currentPath)
                    || string.Equals(currentPath, originalPath, StringComparison.Ordinal))
                    continue;
                if (!declaredPaths.Contains(currentPath))
                    throw new InvalidOperationException(
                        $"Tracked asset '{originalPath}' moved outside the declared transaction scope to '{currentPath}'.");

                string occupantGuid = AssetDatabase.AssetPathToGUID(originalPath);
                if (!string.IsNullOrEmpty(occupantGuid) && occupantGuid != originalGuid)
                    throw new InvalidOperationException(
                        $"Cannot restore tracked asset move to '{originalPath}' because another asset now occupies that path.");

                bool isCaseOnlyRename = string.Equals(currentPath, originalPath, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        Path.GetDirectoryName(currentPath),
                        Path.GetDirectoryName(originalPath),
                        StringComparison.OrdinalIgnoreCase);
                string moveError = isCaseOnlyRename
                    ? AssetDatabase.RenameAsset(
                        currentPath,
                        Path.GetFileNameWithoutExtension(originalPath))
                    : AssetDatabase.MoveAsset(currentPath, originalPath);
                if (!string.IsNullOrEmpty(moveError))
                    throw new IOException(
                        $"Could not restore tracked asset move from '{currentPath}' to '{originalPath}': {moveError}");
            }
        }

        private static Dictionary<string, string> SnapshotAssetGuids(IEnumerable<string> assetPaths)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string path in assetPaths)
            {
                string guid = AssetDatabase.AssetPathToGUID(path);
                if (string.IsNullOrEmpty(guid))
                    continue;
                // On case-insensitive file systems, a not-yet-created destination path can
                // resolve to the source asset when the requested rename changes only casing.
                // Record only the canonical path that actually owns the GUID at transaction start.
                if (string.Equals(AssetDatabase.GUIDToAssetPath(guid), path, StringComparison.Ordinal))
                    result[path] = guid;
            }
            return result;
        }

        private void RestoreDirtyAssetContents(IReadOnlyCollection<string> importedAssetPaths)
        {
            foreach (string path in importedAssetPaths.Where(initialDirtyAssetContents.ContainsKey))
            {
                Dictionary<string, Object> currentObjects = EnumerateSnapshotObjects(
                        Array.Empty<Object>(),
                        Array.Empty<Scene>(),
                        new[] { path })
                    .Where(asset => asset != null)
                    .GroupBy(ObjectIdentity, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

                foreach (AssetObjectState state in initialDirtyAssetContents[path])
                {
                    if (!currentObjects.TryGetValue(state.ObjectId, out Object asset) || asset == null)
                    {
                        asset = state.Asset;
                        if (asset == null || !string.Equals(
                                AssetDatabase.GetAssetPath(asset),
                                path,
                                StringComparison.Ordinal))
                            throw new InvalidOperationException(
                                $"Could not restore the unsaved serialized state of '{state.ObjectPath}' in '{path}'.");
                    }

                    EditorJsonUtility.FromJsonOverwrite(state.Json, asset);
                    EditorUtility.SetDirty(asset);
                }
            }
        }

        private void RestoreMissingAssetFolders()
        {
            bool refreshRequired = false;
            foreach (string folder in initiallyMissingAssetFolders
                .OrderByDescending(path => path.Count(character => character == '/')))
            {
                string fullPath = FullProjectPath(folder);
                if (Directory.Exists(fullPath))
                {
                    if (Directory.EnumerateFileSystemEntries(fullPath).Any())
                        throw new IOException(
                            $"Cannot roll back newly created asset folder '{folder}' because it is not empty.");
                    if (!AssetDatabase.DeleteAsset(folder) && Directory.Exists(fullPath))
                        throw new IOException($"Unity could not remove newly created asset folder '{folder}'.");
                }

                string metaPath = fullPath + ".meta";
                if (File.Exists(metaPath))
                {
                    File.Delete(metaPath);
                    refreshRequired = true;
                }
            }
            if (refreshRequired)
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }

        private void RestoreEditorSetup()
        {
            var currentPrefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (string.IsNullOrEmpty(prefabStageAssetPath))
            {
                if (currentPrefabStage != null)
                    StageUtility.GoToMainStage();
            }
            else if (currentPrefabStage == null || currentPrefabStage.assetPath != prefabStageAssetPath)
            {
                PrefabStageUtility.OpenPrefab(prefabStageAssetPath);
            }

            bool setupChanged = !SameSceneSetup(sceneSetup, EditorSceneManager.GetSceneManagerSetup());
            if (setupChanged)
                EditorSceneManager.RestoreSceneManagerSetup(sceneSetup);
            if (!setupChanged && activeScene.IsValid() && activeScene.isLoaded)
                SceneManager.SetActiveScene(activeScene);

        }

        private static bool SameSceneSetup(SceneSetup[] left, SceneSetup[] right)
        {
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
            {
                if (left[i].path != right[i].path || left[i].isLoaded != right[i].isLoaded || left[i].isActive != right[i].isActive)
                    return false;
            }
            return true;
        }

        private static List<Scene> ResolveTargetScenes(IEnumerable<Object> declaredTargets)
        {
            return declaredTargets
                .Select(OwnerGameObject)
                .Where(go => go != null && go.scene.IsValid() && go.scene.isLoaded)
                .Select(go => go.scene)
                .GroupBy(SceneKey)
                .Select(group => group.First())
                .ToList();
        }

        private static List<string> ResolveTargetAssetPaths(
            IEnumerable<Object> declaredTargets,
            IEnumerable<Scene> scenes,
            IEnumerable<string> additionalPaths)
        {
            List<string> paths = declaredTargets
                .Select(AssetDatabase.GetAssetPath)
                .Concat(scenes.Select(scene => scene.path))
                .Concat(additionalPaths ?? Array.Empty<string>())
                .Where(path => !string.IsNullOrEmpty(path))
                .Select(path => path.Replace('\\', '/'))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            string invalid = paths.FirstOrDefault(path => !path.StartsWith("Assets/", StringComparison.Ordinal) || path.Contains("../"));
            if (invalid != null)
                throw new MutationTransactionException("INVALID_ASSET_PATH", $"Transaction asset path '{invalid}' must be project-relative under Assets/.");
            string folder = paths.FirstOrDefault(AssetDatabase.IsValidFolder);
            if (folder != null)
                throw new MutationTransactionException("INVALID_ASSET_PATH", $"Transaction path '{folder}' identifies a folder, not an authored asset.");
            return paths;
        }

        private static Dictionary<string, byte[]> SnapshotAssetBytes(IEnumerable<string> assetPaths)
        {
            var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (string path in assetPaths)
            {
                string fullPath = FullProjectPath(path);
                result[path] = File.Exists(fullPath) ? File.ReadAllBytes(fullPath) : null;
            }
            return result;
        }

        private static Dictionary<string, List<AssetObjectState>> SnapshotDirtyAssetContents(
            IReadOnlyDictionary<string, bool> dirtyAssets)
        {
            var result = new Dictionary<string, List<AssetObjectState>>(StringComparer.Ordinal);
            foreach (string path in dirtyAssets.Where(pair => pair.Value).Select(pair => pair.Key))
            {
                try
                {
                    result[path] = EnumerateSnapshotObjects(
                            Array.Empty<Object>(),
                            Array.Empty<Scene>(),
                            new[] { path })
                        .Where(asset => asset != null)
                        .Select(AssetObjectState.Capture)
                        .ToList();
                }
                catch (Exception exception)
                {
                    throw new MutationTransactionException(
                        "DIRTY_ASSET_SNAPSHOT_FAILED",
                        $"Could not snapshot the unsaved contents of dirty asset '{path}': {exception.Message}",
                        exception);
                }
            }
            return result;
        }

        private static List<string> SnapshotMissingParentFolders(IEnumerable<string> assetPaths)
        {
            var missing = new HashSet<string>(StringComparer.Ordinal);
            foreach (string assetPath in assetPaths)
            {
                string folder = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
                while (!string.IsNullOrEmpty(folder)
                    && folder.StartsWith("Assets/", StringComparison.Ordinal))
                {
                    if (!Directory.Exists(FullProjectPath(folder)))
                        missing.Add(folder);
                    folder = Path.GetDirectoryName(folder)?.Replace('\\', '/');
                }
            }
            return missing.ToList();
        }

        private static string FullProjectPath(string assetPath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.GetFullPath(Path.Combine(projectRoot, assetPath));
        }

        private static Dictionary<string, Fingerprint> CaptureFingerprints(
            IEnumerable<Object> declaredTargets,
            IEnumerable<Scene> scenes,
            IEnumerable<string> assetPaths)
        {
            var result = new Dictionary<string, Fingerprint>(StringComparer.Ordinal);
            foreach (Object target in EnumerateSnapshotObjects(declaredTargets, scenes, assetPaths))
            {
                try
                {
                    var serialized = new SerializedObject(target);
                    serialized.UpdateIfRequiredOrScript();
                    SerializedProperty property = serialized.GetIterator();
                    bool enterChildren = true;
                    while (property.Next(enterChildren))
                    {
                        // Unity exposes strings through the iterator as array-like values with
                        // synthetic Array.size and Character children, and (6000.5+) object
                        // references with m_FileID/m_PathID children. Both fingerprints already
                        // capture the complete value, so those implementation details must not
                        // become independently guarded changes.
                        enterChildren = property.propertyType != SerializedPropertyType.String
                            && property.propertyType != SerializedPropertyType.ObjectReference;
                        if (IgnoredProperties.Contains(property.propertyPath))
                            continue;
                        Fingerprint fingerprint = Fingerprint.Create(target, property);
                        result[fingerprint.Key] = fingerprint;
                    }
                }
                catch (Exception)
                {
                    // Some native editor objects cannot be serialized. They remain covered by the byte snapshot.
                }
            }
            return result;
        }

        private static IEnumerable<Object> EnumerateSnapshotObjects(
            IEnumerable<Object> declaredTargets,
            IEnumerable<Scene> scenes,
            IEnumerable<string> assetPaths)
        {
            var found = new HashSet<Object>();
            foreach (Object target in declaredTargets)
                if (target != null) found.Add(target);

            foreach (Scene scene in scenes)
            {
                if (!scene.IsValid() || !scene.isLoaded) continue;
                foreach (GameObject root in scene.GetRootGameObjects())
                    AddGameObjectHierarchy(found, root);
            }

            foreach (string path in assetPaths.Where(path => !path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)))
            foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (asset is GameObject root)
                    AddGameObjectHierarchy(found, root);
                else if (asset != null)
                    found.Add(asset);
            }

            return found.OrderBy(ObjectIdentity, StringComparer.Ordinal).ToArray();
        }

        private static void AddGameObjectHierarchy(HashSet<Object> found, GameObject root)
        {
            if (root == null) return;
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
            {
                found.Add(transform.gameObject);
                foreach (Component component in transform.GetComponents<Component>())
                    if (component != null) found.Add(component);
            }
        }

        private static IReadOnlyList<SerializedChange> Diff(
            Dictionary<string, Fingerprint> before,
            Dictionary<string, Fingerprint> after)
        {
            var changes = new List<SerializedChange>();
            foreach (string key in before.Keys.Union(after.Keys).OrderBy(item => item, StringComparer.Ordinal))
            {
                bool hadBefore = before.TryGetValue(key, out Fingerprint oldValue);
                bool hasAfter = after.TryGetValue(key, out Fingerprint newValue);
                if (hadBefore && hasAfter && oldValue.Value == newValue.Value)
                    continue;
                Fingerprint identity = hasAfter ? newValue : oldValue;
                changes.Add(new SerializedChange
                {
                    Kind = hadBefore ? (hasAfter ? "modified" : "removed") : "added",
                    ObjectId = identity.ObjectId,
                    ObjectPath = identity.ObjectPath,
                    AssetPath = identity.AssetPath,
                    ComponentType = identity.ComponentType,
                    Property = identity.Property,
                    Before = hadBefore ? oldValue.Value : null,
                    After = hasAfter ? newValue.Value : null
                });
            }
            return changes;
        }

        private static bool SameChanges(
            IReadOnlyList<SerializedChange> left,
            IReadOnlyList<SerializedChange> right,
            IReadOnlyDictionary<string, string> identityAliases = null)
        {
            if (left.Count != right.Count) return false;

            SerializedChange[] orderedLeft = left
                .OrderBy(change => change.Kind, StringComparer.Ordinal)
                .ThenBy(change => change.ObjectId, StringComparer.Ordinal)
                .ThenBy(change => change.ObjectPath, StringComparer.Ordinal)
                .ThenBy(change => change.AssetPath, StringComparer.Ordinal)
                .ThenBy(change => change.ComponentType, StringComparer.Ordinal)
                .ThenBy(change => change.Property, StringComparer.Ordinal)
                .ThenBy(change => change.Before, StringComparer.Ordinal)
                .ThenBy(change => change.After, StringComparer.Ordinal)
                .ToArray();
            SerializedChange[] orderedRight = right
                .Select(change => NormalizeIdentityAliases(change, identityAliases))
                .OrderBy(change => change.Kind, StringComparer.Ordinal)
                .ThenBy(change => change.ObjectId, StringComparer.Ordinal)
                .ThenBy(change => change.ObjectPath, StringComparer.Ordinal)
                .ThenBy(change => change.AssetPath, StringComparer.Ordinal)
                .ThenBy(change => change.ComponentType, StringComparer.Ordinal)
                .ThenBy(change => change.Property, StringComparer.Ordinal)
                .ThenBy(change => change.Before, StringComparer.Ordinal)
                .ThenBy(change => change.After, StringComparer.Ordinal)
                .ToArray();

            for (int i = 0; i < left.Count; i++)
            {
                SerializedChange a = orderedLeft[i];
                SerializedChange b = orderedRight[i];
                if (a.Kind != b.Kind || a.ObjectId != b.ObjectId || a.ObjectPath != b.ObjectPath
                    || a.AssetPath != b.AssetPath || a.ComponentType != b.ComponentType
                    || a.Property != b.Property || a.Before != b.Before || a.After != b.After)
                    return false;
            }
            return true;
        }

        private Dictionary<int, string> CaptureObjectIdentities()
        {
            return EnumerateSnapshotObjects(targets, targetScenes, targetAssetPaths)
                .Where(target => target != null)
                .ToDictionary(target => target.GetInstanceIDCompat(), ObjectIdentity);
        }

        private IReadOnlyDictionary<string, string> CaptureStabilizedIdentityAliases(
            IReadOnlyDictionary<int, string> identitiesBeforeSave)
        {
            var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Object target in EnumerateSnapshotObjects(targets, targetScenes, targetAssetPaths))
            {
                if (target == null || !identitiesBeforeSave.TryGetValue(target.GetInstanceIDCompat(), out string beforeId)
                    || !beforeId.StartsWith("temporary:", StringComparison.Ordinal))
                    continue;

                string afterId = ObjectIdentity(target);
                if (afterId != beforeId)
                    aliases[afterId] = beforeId;
            }
            return aliases;
        }

        private static SerializedChange NormalizeIdentityAliases(
            SerializedChange change,
            IReadOnlyDictionary<string, string> identityAliases)
        {
            if (identityAliases == null || identityAliases.Count == 0)
                return change;
            return new SerializedChange
            {
                Kind = change.Kind,
                ObjectId = NormalizeIdentityAlias(change.ObjectId, identityAliases),
                ObjectPath = change.ObjectPath,
                AssetPath = change.AssetPath,
                ComponentType = change.ComponentType,
                Property = change.Property,
                Before = NormalizeIdentityAlias(change.Before, identityAliases),
                After = NormalizeIdentityAlias(change.After, identityAliases)
            };
        }

        private static string NormalizeIdentityAlias(
            string value,
            IReadOnlyDictionary<string, string> identityAliases)
        {
            return value != null && identityAliases.TryGetValue(value, out string alias) ? alias : value;
        }

        private static string ObjectIdentity(Object target)
        {
            GlobalObjectId id = GlobalObjectId.GetGlobalObjectIdSlow(target);
            string stable = id.ToString();
            if (!stable.EndsWith("-0-0", StringComparison.Ordinal))
                return stable;
            return $"temporary:{ObjectPath(target)}:{target.GetType().FullName}:{target.GetInstanceIDCompat()}";
        }

        private static string ObjectPath(Object target)
        {
            GameObject go = OwnerGameObject(target);
            if (go != null)
            {
                var names = new Stack<string>();
                Transform current = go.transform;
                while (current != null)
                {
                    names.Push(current.name);
                    current = current.parent;
                }
                return string.Join("/", names.ToArray());
            }
            string assetPath = AssetDatabase.GetAssetPath(target);
            return string.IsNullOrEmpty(assetPath) ? target.name : assetPath;
        }

        private static GameObject OwnerGameObject(Object target)
        {
            if (target is GameObject go) return go;
            return (target as Component)?.gameObject;
        }

        private static string SceneKey(Scene scene) => string.IsNullOrEmpty(scene.path) ? scene.name : scene.path;

        private void EnsureOpen()
        {
            if (finished)
                throw new InvalidOperationException("The mutation transaction has already completed.");
        }

        private sealed class Fingerprint
        {
            public string Key;
            public string ObjectId;
            public string ObjectPath;
            public string AssetPath;
            public string ComponentType;
            public string Property;
            public string Value;

            public static Fingerprint Create(Object target, SerializedProperty property)
            {
                string objectId = ObjectIdentity(target);
                string componentType = target.GetType().FullName;
                return new Fingerprint
                {
                    Key = $"{objectId}|{componentType}|{property.propertyPath}",
                    ObjectId = objectId,
                    ObjectPath = MutationTransaction.ObjectPath(target),
                    AssetPath = AssetDatabase.GetAssetPath(target),
                    ComponentType = componentType,
                    Property = property.propertyPath,
                    Value = Normalize(property)
                };
            }

            private static string Normalize(SerializedProperty property)
            {
                switch (property.propertyType)
                {
                    case SerializedPropertyType.Integer: return property.longValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    case SerializedPropertyType.Boolean: return property.boolValue ? "true" : "false";
                    case SerializedPropertyType.Float: return property.doubleValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                    case SerializedPropertyType.String: return property.stringValue ?? "<null>";
                    case SerializedPropertyType.Color: return property.colorValue.ToString("R");
                    case SerializedPropertyType.ObjectReference:
                        return property.objectReferenceValue == null
                            ? NormalizeMissingReference(property)
                            : ObjectIdentity(property.objectReferenceValue);
                    case SerializedPropertyType.LayerMask: return property.intValue.ToString();
                    case SerializedPropertyType.Enum: return property.enumValueIndex.ToString();
                    case SerializedPropertyType.Vector2: return property.vector2Value.ToString("R");
                    case SerializedPropertyType.Vector3: return property.vector3Value.ToString("R");
                    case SerializedPropertyType.Vector4: return property.vector4Value.ToString("R");
                    case SerializedPropertyType.Rect: return property.rectValue.ToString();
                    case SerializedPropertyType.ArraySize: return property.intValue.ToString();
                    case SerializedPropertyType.Character: return property.intValue.ToString();
                    case SerializedPropertyType.AnimationCurve: return NormalizeCurve(property.animationCurveValue);
                    case SerializedPropertyType.Bounds: return property.boundsValue.ToString();
                    case SerializedPropertyType.Quaternion: return property.quaternionValue.ToString("R");
                    case SerializedPropertyType.Vector2Int: return property.vector2IntValue.ToString();
                    case SerializedPropertyType.Vector3Int: return property.vector3IntValue.ToString();
                    case SerializedPropertyType.RectInt: return property.rectIntValue.ToString();
                    case SerializedPropertyType.BoundsInt: return property.boundsIntValue.ToString();
                    case SerializedPropertyType.ManagedReference: return property.managedReferenceFullTypename ?? "null";
                    default: return property.type ?? property.propertyType.ToString();
                }
            }

            private static string NormalizeMissingReference(SerializedProperty property)
            {
#if UNITY_6000_5_OR_NEWER
                ulong entityId = EntityId.ToULong(property.objectReferenceEntityIdValue);
                return entityId == 0
                    ? "null"
                    : $"missing:{entityId}";
#else
                return property.objectReferenceInstanceIDValue == 0
                    ? "null"
                    : $"missing:{property.objectReferenceInstanceIDValue}";
#endif
            }

            private static string NormalizeCurve(AnimationCurve curve)
            {
                if (curve == null) return "null";
                var builder = new StringBuilder();
                builder.Append((int)curve.preWrapMode).Append('|').Append((int)curve.postWrapMode);
                foreach (Keyframe key in curve.keys)
                {
                    builder.Append('|')
                        .Append(key.time.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                        .Append(key.value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                        .Append(key.inTangent.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                        .Append(key.outTangent.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                        .Append(key.inWeight.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                        .Append(key.outWeight.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                        .Append((int)key.weightedMode);
                }
                return builder.ToString();
            }
        }

        private sealed class AssetObjectState
        {
            public Object Asset;
            public string ObjectId;
            public string ObjectPath;
            public string Json;

            public static AssetObjectState Capture(Object asset)
            {
                return new AssetObjectState
                {
                    Asset = asset,
                    ObjectId = ObjectIdentity(asset),
                    ObjectPath = MutationTransaction.ObjectPath(asset),
                    Json = EditorJsonUtility.ToJson(asset)
                };
            }
        }
    }
}
