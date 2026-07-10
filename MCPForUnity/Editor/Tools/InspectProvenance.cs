using System;
using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>Read-only compact scene and prefab provenance inspection.</summary>
    [McpForUnityTool("inspect_provenance", AutoRegister = true, Group = "core")]
    public static class InspectProvenance
    {
        private const int DefaultPageSize = 10;
        private const int DefaultOverrideLimit = 20;
        private const int DefaultComponentLimit = 10;
        private const int MaxPageSize = 200;
        private const int MaxComponentLimit = 100;
        private const int MaxNestedRecordsPerPage = 500;

        public static object HandleCommand(JObject @params)
        {
            if (@params == null)
                return Error("INVALID_PARAMS", "Parameters cannot be null.");

            var p = new ToolParams(@params);
            string[] targets = p.GetStringArray("targets") ?? Array.Empty<string>();
            if (targets.Length == 0 || targets.Any(string.IsNullOrWhiteSpace))
                return Error("INVALID_PARAMS", "'targets' must be a non-empty array of names, paths, asset paths, or GlobalObjectIds.");
            if (targets.Length > MaxPageSize)
                return Error("PAYLOAD_LIMIT_EXCEEDED", $"At most {MaxPageSize} targets may be inspected per request.");

            bool includeInactive = p.GetBool("includeInactive", true);
            bool includeProperties = p.GetBool("includePropertyOverrides", true);
            bool includeComponents = p.GetBool("includeComponentOverrides", true);
            int overrideLimit = Math.Min(MaxPageSize, Math.Max(1, p.GetInt("overrideLimit") ?? DefaultOverrideLimit));
            int componentLimit = Math.Min(MaxComponentLimit, Math.Max(1, p.GetInt("componentLimit") ?? DefaultComponentLimit));
            int pageSize = Math.Min(MaxPageSize, Math.Max(1, p.GetInt("pageSize") ?? DefaultPageSize));
            int nestedRecordsPerTarget = (includeProperties ? overrideLimit : 0)
                + (includeComponents ? componentLimit * 2 : 0);
            if (pageSize * nestedRecordsPerTarget > MaxNestedRecordsPerPage)
                return Error(
                    "PAYLOAD_LIMIT_EXCEEDED",
                    $"The requested page and nested provenance limits must not exceed {MaxNestedRecordsPerPage} records.");
            if (!TryReadCursor(p.Get("cursor"), out int offset))
                return Error("INVALID_CURSOR", "'cursor' must be a non-negative integer returned by this tool.");

            var findings = new List<object>();
            foreach (string requested in targets)
            {
                if (!TryResolve(requested, includeInactive, out UnityEngine.Object target, out string code, out string message))
                    return Error(code, message, new { target = requested });

                findings.Add(BuildFinding(
                    requested,
                    target,
                    includeProperties,
                    includeComponents,
                    overrideLimit,
                    componentLimit));
            }

            object[] page = findings.Skip(offset).Take(pageSize).ToArray();
            int nextOffset = offset + page.Length;
            return new SuccessResponse(
                $"Inspected provenance for {page.Length} target(s).",
                new
                {
                    findings = page,
                    total = findings.Count,
                    next_cursor = nextOffset < findings.Count ? nextOffset.ToString() : null,
                    page_size = pageSize
                });
        }

        private static object BuildFinding(
            string requested,
            UnityEngine.Object target,
            bool includeProperties,
            bool includeComponents,
            int overrideLimit,
            int componentLimit)
        {
            GameObject gameObject = OwnerGameObject(target);
            bool isInstance = gameObject != null && PrefabUtility.IsPartOfPrefabInstance(gameObject);
            GameObject nearestRoot = isInstance ? PrefabUtility.GetNearestPrefabInstanceRoot(gameObject) : null;
            GameObject outermostRoot = isInstance ? PrefabUtility.GetOutermostPrefabInstanceRoot(gameObject) : null;
            UnityEngine.Object source = isInstance ? PrefabUtility.GetCorrespondingObjectFromSource(target) : null;
            GameObject sourceGameObject = source as GameObject ?? (source as Component)?.gameObject;
            string directAssetPath = AssetDatabase.GetAssetPath(target);
            string sourceAssetPath = source != null ? AssetDatabase.GetAssetPath(source) : null;
            string assetPath = !string.IsNullOrEmpty(directAssetPath) ? directAssetPath : sourceAssetPath;

            int totalPropertyOverrides = 0;
            List<object> propertyOverrides = includeProperties && isInstance
                ? PropertyOverrides(gameObject, sourceGameObject, nearestRoot, overrideLimit, out totalPropertyOverrides)
                : new List<object>();

            List<object> allAddedComponents = includeComponents && isInstance
                ? AddedComponents(gameObject, nearestRoot)
                : new List<object>();
            List<object> allRemovedComponents = includeComponents && isInstance
                ? RemovedComponents(sourceGameObject, nearestRoot)
                : new List<object>();
            List<object> addedComponents = allAddedComponents.Take(componentLimit).ToList();
            List<object> removedComponents = allRemovedComponents.Take(componentLimit).ToList();

            return new
            {
                target = requested,
                path = gameObject != null ? GameObjectLookup.GetGameObjectPath(gameObject) : directAssetPath,
                global_object_id = GlobalIdOf(target),
                asset_path = EmptyToNull(assetPath),
                asset_guid = AssetGuid(assetPath),
                scene_path = gameObject != null && gameObject.scene.IsValid() ? EmptyToNull(gameObject.scene.path) : null,
                object_type = target.GetType().FullName,
                prefab = new
                {
                    is_instance = isInstance,
                    instance_status = isInstance ? PrefabUtility.GetPrefabInstanceStatus(gameObject).ToString() : null,
                    instance_root = Identity(nearestRoot),
                    nearest_instance_root = Identity(nearestRoot),
                    outermost_instance_root = Identity(outermostRoot),
                    source_object = Identity(source),
                    source_asset = EmptyToNull(sourceAssetPath),
                    has_any_overrides = nearestRoot != null && PrefabUtility.HasPrefabInstanceAnyOverrides(nearestRoot, false),
                    override_property_paths = includeProperties ? propertyOverrides : null,
                    override_property_count = includeProperties ? totalPropertyOverrides : 0,
                    overrides_truncated = includeProperties && totalPropertyOverrides > propertyOverrides.Count,
                    added_components = includeComponents ? addedComponents : null,
                    added_component_count = includeComponents ? allAddedComponents.Count : 0,
                    removed_components = includeComponents ? removedComponents : null,
                    removed_component_count = includeComponents ? allRemovedComponents.Count : 0,
                    component_overrides_truncated = includeComponents
                        && (allAddedComponents.Count > addedComponents.Count
                            || allRemovedComponents.Count > removedComponents.Count)
                }
            };
        }

        private static List<object> PropertyOverrides(
            GameObject target,
            GameObject sourceTarget,
            GameObject instanceRoot,
            int limit,
            out int total)
        {
            var records = new List<object>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            PropertyModification[] modifications = PrefabUtility.GetPropertyModifications(instanceRoot) ?? Array.Empty<PropertyModification>();
            foreach (PropertyModification modification in modifications)
            {
                if (modification == null || modification.target == null || !BelongsToSourceObject(modification.target, sourceTarget))
                    continue;

                string key = modification.target.GetType().FullName + "\n" + modification.propertyPath;
                if (!seen.Add(key)) continue;

                records.Add(new
                {
                    component = modification.target.GetType().FullName,
                    property = modification.propertyPath,
                    source_global_object_id = GlobalIdOf(modification.target)
                });
            }

            total = records.Count;
            return records.Take(limit).ToList();
        }

        private static bool BelongsToSourceObject(UnityEngine.Object target, GameObject sourceTarget)
        {
            if (sourceTarget == null) return false;
            if (target == sourceTarget) return true;
            return target is Component component && component.gameObject == sourceTarget;
        }

        private static List<object> AddedComponents(GameObject target, GameObject instanceRoot)
        {
            if (instanceRoot == null) return new List<object>();
            return PrefabUtility.GetAddedComponents(instanceRoot)
                .Where(item => item.instanceComponent != null && item.instanceComponent.gameObject == target)
                .Select(item => (object)new
                {
                    type = item.instanceComponent.GetType().FullName,
                    global_object_id = GlobalIdOf(item.instanceComponent)
                })
                .ToList();
        }

        private static List<object> RemovedComponents(GameObject sourceTarget, GameObject instanceRoot)
        {
            if (instanceRoot == null || sourceTarget == null) return new List<object>();
            return PrefabUtility.GetRemovedComponents(instanceRoot)
                .Where(item => item.assetComponent != null && item.assetComponent.gameObject == sourceTarget)
                .Select(item => (object)new
                {
                    type = item.assetComponent.GetType().FullName,
                    source_global_object_id = GlobalIdOf(item.assetComponent)
                })
                .ToList();
        }

        private static object Identity(UnityEngine.Object target)
        {
            if (target == null) return null;
            GameObject gameObject = OwnerGameObject(target);
            return new
            {
                name = target.name,
                path = gameObject != null ? GameObjectLookup.GetGameObjectPath(gameObject) : AssetDatabase.GetAssetPath(target),
                global_object_id = GlobalIdOf(target),
                type = target.GetType().FullName
            };
        }

        private static bool TryResolve(
            string requested,
            bool includeInactive,
            out UnityEngine.Object target,
            out string code,
            out string message)
        {
            target = null;
            code = null;
            message = null;

            if (GlobalObjectId.TryParse(requested, out GlobalObjectId globalId))
            {
                target = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(globalId);
                if (target != null) return true;
                code = "TARGET_NOT_FOUND";
                message = $"GlobalObjectId '{requested}' could not be resolved in the current editor state.";
                return false;
            }

            if (requested.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                || requested.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                target = AssetDatabase.LoadMainAssetAtPath(requested);
                if (target != null) return true;
                code = "TARGET_NOT_FOUND";
                message = $"Asset target '{requested}' was not found.";
                return false;
            }

            string method = requested.Contains("/") ? "by_path" : "by_name";
            List<int> matches = GameObjectLookup.SearchGameObjects(method, requested, includeInactive, 2);
            if (matches.Count == 0)
            {
                code = "TARGET_NOT_FOUND";
                message = $"Provenance target '{requested}' was not found.";
                return false;
            }
            if (matches.Count > 1)
            {
                code = "TARGET_AMBIGUOUS";
                message = $"Provenance target '{requested}' matched multiple objects; use a hierarchy path or GlobalObjectId.";
                return false;
            }

            target = GameObjectLookup.FindById(matches[0]);
            return target != null;
        }

        private static GameObject OwnerGameObject(UnityEngine.Object target)
        {
            if (target is GameObject gameObject) return gameObject;
            return (target as Component)?.gameObject;
        }

        private static string GlobalIdOf(UnityEngine.Object target)
        {
            return target == null ? null : GlobalObjectId.GetGlobalObjectIdSlow(target).ToString();
        }

        private static string AssetGuid(string assetPath)
        {
            return string.IsNullOrEmpty(assetPath) ? null : EmptyToNull(AssetDatabase.AssetPathToGUID(assetPath));
        }

        private static string EmptyToNull(string value) => string.IsNullOrEmpty(value) ? null : value;

        private static bool TryReadCursor(string cursor, out int offset)
        {
            if (string.IsNullOrWhiteSpace(cursor))
            {
                offset = 0;
                return true;
            }
            return int.TryParse(cursor, out offset) && offset >= 0;
        }

        private static ErrorResponse Error(string code, string message, object details = null) =>
            new ErrorResponse(code, new { message, details });
    }
}
