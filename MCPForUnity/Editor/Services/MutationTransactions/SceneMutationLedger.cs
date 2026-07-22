using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MCPForUnity.Editor.Services.MutationTransactions
{
    /// <summary>
    /// Per-scene record of the objects this editor session INTENTIONALLY mutated, kept so the
    /// scoped scene save can distinguish deliberate edits from ambient in-memory drift
    /// ([ExecuteAlways] preview writers, layout-driven RectTransforms, TMP re-baking, ...) that
    /// accumulates while a scene is open and would otherwise be baked to disk by a full save.
    ///
    /// The only feed is explicit <see cref="Record"/>/<see cref="RecordDeletion"/> calls from the
    /// MCP mutation tools. An Undo.postprocessModifications hook was tried and rejected: TMP
    /// re-baking and layout-driven RectTransform writers flush through the undo pipeline too, so
    /// the hook swept the very drift this ledger exists to exclude. Consequently edits made
    /// outside instrumented call sites (Inspector edits, execute_code, and MCP tools that do not
    /// yet call Record — coverage today is ComponentOps, ManageComponents, and the GameObjects
    /// tools) surface as reported unscoped drift; use a normal editor save or
    /// unscoped_changes=include (the default) to persist those.
    ///
    /// Entries are keyed by <see cref="GlobalObjectId"/> string; unsaved objects (no fileID yet)
    /// are skipped — their scene-file blocks are new and the merge includes new blocks
    /// unconditionally. The ledger survives domain reloads via <see cref="SessionState"/> and is
    /// cleared when a scene is saved, closed, reopened, undone, or redone. <see cref="MutationTransaction"/>
    /// captures a <see cref="Snapshot"/> on begin and restores it on rollback, so Record calls
    /// made by a previewed or rejected mutation do not outlive the reverted edit.
    /// </summary>
    [InitializeOnLoad]
    public static class SceneMutationLedger
    {
        private const string SessionKey = "MCPForUnity.SceneMutationLedger";

        // Persisted with Newtonsoft (SessionState JSON), not Unity serialization.
        internal sealed class SceneEntries
        {
            public HashSet<string> Touched = new HashSet<string>(StringComparer.Ordinal);
            public HashSet<string> Deleted = new HashSet<string>(StringComparer.Ordinal);
            public Dictionary<string, HashSet<string>> PropertyPaths =
                new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        }

        // Scene path -> entries. Scene path (not guid) so entries die naturally with a rename.
        private static Dictionary<string, SceneEntries> ledger =
            new Dictionary<string, SceneEntries>(StringComparer.Ordinal);

        /// <summary>Set while the scoped save runs so its own SaveScene(saveAsCopy) does not clear the ledger.</summary>
        internal static bool SuppressSceneSavedClear;

        /// <summary>Set while a transaction performs its internal Undo-based rollback.</summary>
        internal static bool SuppressUndoRedoClear;

        static SceneMutationLedger()
        {
            Restore();
            EditorSceneManager.sceneSaved += OnSceneSaved;
            EditorSceneManager.sceneClosed += scene => Clear(scene.path);
            EditorSceneManager.sceneOpened += (scene, _) => Clear(scene.path);
            Undo.undoRedoPerformed += OnUndoRedoPerformed;
            AssemblyReloadEvents.beforeAssemblyReload += Persist;
        }

        /// <summary>Marks <paramref name="target"/> (a scene GameObject or Component) as intentionally mutated.</summary>
        public static void Record(Object target, string propertyPath = null)
        {
            if (target == null || EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            GameObject owner = target as GameObject ?? (target as Component)?.gameObject;
            if (owner == null || !owner.scene.IsValid() || string.IsNullOrEmpty(owner.scene.path))
                return;

            string id = SavedGlobalObjectId(target);
            if (id == null)
                return; // Unsaved object: its scene-file block is new, which the merge scopes automatically.

            SceneEntries entries = EntriesFor(owner.scene.path);
            entries.Touched.Add(id);
            if (!string.IsNullOrEmpty(propertyPath))
            {
                if (!entries.PropertyPaths.TryGetValue(id, out HashSet<string> paths))
                    entries.PropertyPaths[id] = paths = new HashSet<string>(StringComparer.Ordinal);
                paths.Add(propertyPath);
            }
        }

        /// <summary>
        /// Marks the whole hierarchy under <paramref name="root"/> (GameObjects + components) as
        /// intentionally deleted. Must be called BEFORE the destroy — GlobalObjectIds are
        /// unreadable afterwards.
        /// </summary>
        public static void RecordDeletion(GameObject root)
        {
            if (root == null || EditorApplication.isPlayingOrWillChangePlaymode
                || !root.scene.IsValid() || string.IsNullOrEmpty(root.scene.path))
                return;

            SceneEntries entries = EntriesFor(root.scene.path);
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
            {
                AddDeletion(entries, transform.gameObject);
                foreach (Component component in transform.GetComponents<Component>())
                    if (component != null)
                        AddDeletion(entries, component);
            }
        }

        /// <summary>Marks a single component as intentionally deleted. Call BEFORE the destroy.</summary>
        public static void RecordDeletion(Component component)
        {
            if (component == null || EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            Scene scene = component.gameObject.scene;
            if (!scene.IsValid() || string.IsNullOrEmpty(scene.path))
                return;
            AddDeletion(EntriesFor(scene.path), component);
        }

        /// <summary>The intentionally touched / deleted GlobalObjectId strings recorded for a scene.</summary>
        public static (IReadOnlyCollection<string> touched, IReadOnlyCollection<string> deleted)
            SnapshotForScene(string scenePath)
        {
            string key = Normalize(scenePath);
            if (key == null || !ledger.TryGetValue(key, out SceneEntries entries))
                return (Array.Empty<string>(), Array.Empty<string>());
            return (entries.Touched.ToArray(), entries.Deleted.ToArray());
        }

        /// <summary>Opaque deep copy of the whole ledger, taken by a mutation transaction on begin.</summary>
        internal sealed class Snapshot
        {
            internal readonly Dictionary<string, SceneEntries> Entries;
            internal Snapshot(Dictionary<string, SceneEntries> entries) { Entries = entries; }
        }

        internal static Snapshot CaptureSnapshot()
        {
            var copy = new Dictionary<string, SceneEntries>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, SceneEntries> pair in ledger)
                copy[pair.Key] = new SceneEntries
                {
                    Touched = new HashSet<string>(pair.Value.Touched, StringComparer.Ordinal),
                    Deleted = new HashSet<string>(pair.Value.Deleted, StringComparer.Ordinal),
                    PropertyPaths = pair.Value.PropertyPaths.ToDictionary(
                        entry => entry.Key,
                        entry => new HashSet<string>(entry.Value, StringComparer.Ordinal),
                        StringComparer.Ordinal),
                };
            return new Snapshot(copy);
        }

        /// <summary>Discards every Record made since the snapshot (the transaction rolled back).</summary>
        internal static void RestoreSnapshot(Snapshot snapshot)
        {
            if (snapshot != null)
                ledger = snapshot.Entries;
        }

        /// <summary>Drops all entries for a scene (it was saved, closed, or reloaded from disk).</summary>
        public static void Clear(string scenePath)
        {
            string key = Normalize(scenePath);
            if (key != null)
                ledger.Remove(key);
        }

        private static void OnSceneSaved(Scene scene)
        {
            if (!SuppressSceneSavedClear)
                Clear(scene.path);
        }

        private static void OnUndoRedoPerformed()
        {
            // Ledger entries describe the post-mutation state. Unity's Undo API does not expose
            // enough durable per-object/group identity here to reverse those entries reliably,
            // so invalidate them all rather than letting a stale scope bake ambient drift.
            if (!SuppressUndoRedoClear)
                ledger.Clear();
        }

        private static void AddDeletion(SceneEntries entries, Object target)
        {
            string id = SavedGlobalObjectId(target);
            if (id != null)
                entries.Deleted.Add(id);
        }

        /// <summary>GlobalObjectId string, or null for objects that have never been saved (no fileID).</summary>
        private static string SavedGlobalObjectId(Object target)
        {
            GlobalObjectId id = GlobalObjectId.GetGlobalObjectIdSlow(target);
            return id.targetObjectId == 0 && id.targetPrefabId == 0 ? null : id.ToString();
        }

        private static SceneEntries EntriesFor(string scenePath)
        {
            string key = Normalize(scenePath);
            if (!ledger.TryGetValue(key, out SceneEntries entries))
                ledger[key] = entries = new SceneEntries();
            return entries;
        }

        private static string Normalize(string scenePath) =>
            string.IsNullOrEmpty(scenePath) ? null : scenePath.Replace('\\', '/');

        private static void Persist()
        {
            try
            {
                SessionState.SetString(SessionKey, JsonConvert.SerializeObject(ledger));
            }
            catch
            {
                // Best-effort: losing the ledger over a domain reload degrades to "everything is drift",
                // which the save surfaces loudly rather than silently mis-saving.
            }
        }

        private static void Restore()
        {
            try
            {
                string json = SessionState.GetString(SessionKey, null);
                if (!string.IsNullOrEmpty(json))
                    ledger = JsonConvert.DeserializeObject<Dictionary<string, SceneEntries>>(json)
                             ?? new Dictionary<string, SceneEntries>(StringComparer.Ordinal);
            }
            catch
            {
                ledger = new Dictionary<string, SceneEntries>(StringComparer.Ordinal);
            }
        }
    }
}
