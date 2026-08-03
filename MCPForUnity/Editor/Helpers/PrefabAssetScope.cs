using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MCPForUnity.Editor.Helpers
{
    /// <summary>
    /// Read-only, headless access to a prefab asset's hierarchy.
    /// </summary>
    internal sealed class PrefabAssetScope : IDisposable
    {
        public string AssetPath { get; }
        public GameObject Root { get; private set; }

        private readonly GameObject _assetRoot;
        private readonly bool _ownsLoadedContents;

        private PrefabAssetScope(
            string assetPath,
            GameObject root,
            GameObject assetRoot,
            bool ownsLoadedContents)
        {
            AssetPath = assetPath;
            Root = root;
            _assetRoot = assetRoot;
            _ownsLoadedContents = ownsLoadedContents;
        }

        public static bool TryOpen(
            string requestedPath,
            out PrefabAssetScope scope,
            out string errorCode,
            out string errorMessage)
        {
            scope = null;
            errorCode = null;
            errorMessage = null;

            if (!TryNormalizePrefabPath(requestedPath, out string assetPath))
            {
                errorCode = "INVALID_ASSET_PATH";
                errorMessage = "'prefab_path' must be a project-relative .prefab path under Assets/ or Packages/.";
                return false;
            }

            GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefabAsset == null || PrefabUtility.GetPrefabAssetType(prefabAsset) == PrefabAssetType.NotAPrefab)
            {
                errorCode = "PREFAB_NOT_FOUND";
                errorMessage = $"Prefab asset '{assetPath}' was not found.";
                return false;
            }

            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage?.prefabContentsRoot != null
                && PathsEqual(prefabStage.assetPath, assetPath))
            {
                scope = new PrefabAssetScope(
                    assetPath,
                    prefabStage.prefabContentsRoot,
                    prefabAsset,
                    ownsLoadedContents: false);
                return true;
            }

            try
            {
                GameObject root = PrefabUtility.LoadPrefabContents(assetPath);
                if (root == null)
                {
                    errorCode = "PREFAB_LOAD_FAILED";
                    errorMessage = $"Failed to load prefab contents from '{assetPath}'.";
                    return false;
                }

                scope = new PrefabAssetScope(
                    assetPath,
                    root,
                    prefabAsset,
                    ownsLoadedContents: true);
                return true;
            }
            catch (Exception exception)
            {
                errorCode = "PREFAB_LOAD_FAILED";
                errorMessage = $"Failed to load prefab contents from '{assetPath}': {exception.Message}";
                return false;
            }
        }

        /// <summary>Maps a stable prefab-asset GlobalObjectId back into this scope's live hierarchy.</summary>
        public bool TryResolveGlobalObjectId(
            string requested,
            out UnityEngine.Object target,
            out bool parsed)
        {
            target = null;
            parsed = GlobalObjectId.TryParse(requested, out GlobalObjectId globalId);
            if (!parsed) return false;

            UnityEngine.Object assetObject = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(globalId);
            if (assetObject == null || !PathsEqual(AssetDatabase.GetAssetPath(assetObject), AssetPath))
                return false;

            target = PreviewObjectFor(assetObject);
            return target != null;
        }

        /// <summary>
        /// Returns a persistent asset object for a preview/live-stage object when that identity
        /// is stable and belongs to the inspected prefab. Nested-source members intentionally
        /// return null because a source-asset id cannot identify a particular nested instance.
        /// </summary>
        public UnityEngine.Object StableAssetObjectFor(UnityEngine.Object candidate)
        {
            if (candidate == null) return null;
            if (AssetDatabase.Contains(candidate)) return candidate;

            UnityEngine.Object source = PrefabUtility.GetCorrespondingObjectFromSource(candidate);
            if (source != null && PathsEqual(AssetDatabase.GetAssetPath(source), AssetPath))
                return source;

            UnityEngine.Object mapped = MapObject(candidate, Root, _assetRoot);
            return mapped != null && PathsEqual(AssetDatabase.GetAssetPath(mapped), AssetPath)
                ? mapped
                : null;
        }

        public List<GameObject> FindGameObjects(string requested, bool includeInactive, int maxResults = 0)
        {
            var matches = new List<GameObject>();
            if (Root == null || string.IsNullOrWhiteSpace(requested))
                return matches;

            bool matchPath = requested.Contains("/");
            foreach (GameObject candidate in Enumerate(Root, includeInactive))
            {
                bool matchesRequest = matchPath
                    ? GameObjectLookup.MatchesPath(candidate, requested)
                    : string.Equals(candidate.name, requested, StringComparison.Ordinal);
                if (!matchesRequest) continue;

                matches.Add(candidate);
                if (maxResults > 0 && matches.Count >= maxResults)
                    break;
            }
            return matches;
        }

        private static IEnumerable<GameObject> Enumerate(GameObject root, bool includeInactive)
        {
            // Prefab contents live in an isolated preview scene, so authored activity is
            // represented by activeSelf rather than the surrounding scene's active state.
            if (!includeInactive && !root.activeSelf)
                yield break;

            yield return root;
            foreach (Transform child in root.transform)
            {
                foreach (GameObject descendant in Enumerate(child.gameObject, includeInactive))
                    yield return descendant;
            }
        }

        private UnityEngine.Object PreviewObjectFor(UnityEngine.Object assetObject)
        {
            // Corresponding-source links survive unsaved renames/reorders in Prefab Stage.
            foreach (UnityEngine.Object candidate in EnumerateObjects(Root))
            {
                if (PrefabUtility.GetCorrespondingObjectFromSource(candidate) == assetObject)
                    return candidate;
            }

            return MapObject(assetObject, _assetRoot, Root);
        }

        private static IEnumerable<UnityEngine.Object> EnumerateObjects(GameObject root)
        {
            if (root == null) yield break;
            foreach (GameObject candidate in Enumerate(root, includeInactive: true))
            {
                yield return candidate;
                foreach (Component component in candidate.GetComponents<Component>())
                {
                    if (component != null) yield return component;
                }
            }
        }

        private static UnityEngine.Object MapObject(
            UnityEngine.Object candidate,
            GameObject fromRoot,
            GameObject toRoot)
        {
            if (candidate == null || fromRoot == null || toRoot == null) return null;

            GameObject candidateOwner = candidate as GameObject ?? (candidate as Component)?.gameObject;
            if (candidateOwner == null) return null;

            var siblingRoute = new Stack<int>();
            Transform cursor = candidateOwner.transform;
            while (cursor != null && cursor != fromRoot.transform)
            {
                siblingRoute.Push(cursor.GetSiblingIndex());
                cursor = cursor.parent;
            }
            if (cursor != fromRoot.transform) return null;

            Transform mappedTransform = toRoot.transform;
            while (siblingRoute.Count > 0)
            {
                int siblingIndex = siblingRoute.Pop();
                if (siblingIndex < 0 || siblingIndex >= mappedTransform.childCount) return null;
                mappedTransform = mappedTransform.GetChild(siblingIndex);
            }

            if (candidate is GameObject) return mappedTransform.gameObject;
            if (!(candidate is Component candidateComponent)) return null;

            Type componentType = candidateComponent.GetType();
            Component[] sourceComponents = candidateOwner.GetComponents(componentType);
            int componentIndex = Array.IndexOf(sourceComponents, candidateComponent);
            Component[] mappedComponents = mappedTransform.gameObject.GetComponents(componentType);
            return componentIndex >= 0 && componentIndex < mappedComponents.Length
                ? mappedComponents[componentIndex]
                : null;
        }

        private static bool TryNormalizePrefabPath(string requestedPath, out string assetPath)
        {
            assetPath = AssetPathUtility.NormalizeSeparators(requestedPath?.Trim());
            if (string.IsNullOrWhiteSpace(assetPath)
                || assetPath.Contains("..")
                || !assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                return false;

            bool rooted = assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                || assetPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase);
            if (!rooted) return false;

            char[] invalidChars = { ':', '*', '?', '"', '<', '>', '|' };
            return assetPath.IndexOfAny(invalidChars) < 0;
        }

        private static bool PathsEqual(string left, string right) =>
            string.Equals(
                AssetPathUtility.NormalizeSeparators(left),
                AssetPathUtility.NormalizeSeparators(right),
                StringComparison.OrdinalIgnoreCase);

        public void Dispose()
        {
            if (Root == null) return;
            if (_ownsLoadedContents)
                PrefabUtility.UnloadPrefabContents(Root);
            Root = null;
        }
    }
}
