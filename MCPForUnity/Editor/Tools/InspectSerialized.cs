using System;
using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>Read-only inspection of explicitly whitelisted serialized properties.</summary>
    [McpForUnityTool("inspect_serialized", AutoRegister = true, Group = "core")]
    public static class InspectSerialized
    {
        private const int DefaultPageSize = 50;
        private const int MaxPageSize = 200;

        private sealed class TargetRequest
        {
            public string Requested;
            public string ComponentType;
        }

        public static object HandleCommand(JObject @params)
        {
            if (@params == null)
                return Error("INVALID_PARAMS", "Parameters cannot be null.");

            var p = new ToolParams(@params);
            JToken targetsToken = p.GetRaw("targets");
            string[] properties = p.GetStringArray("properties") ?? Array.Empty<string>();
            if (!(targetsToken is JArray targetsArray) || targetsArray.Count == 0)
                return Error("INVALID_PARAMS", "'targets' must be a non-empty array.");
            if (properties.Length == 0 || properties.Any(string.IsNullOrWhiteSpace))
                return Error("INVALID_PARAMS", "'properties' must be a non-empty whitelist of serialized property paths.");

            bool includeInactive = p.GetBool("includeInactive", true);
            bool includePrefab = p.GetBool("includePrefabProvenance", true);
            bool includeMissing = p.GetBool("includeMissingReferences", true);
            int pageSize = Math.Min(MaxPageSize, Math.Max(1, p.GetInt("pageSize") ?? DefaultPageSize));
            if (!TryReadCursor(p.Get("cursor"), out int offset))
                return Error("INVALID_CURSOR", "'cursor' must be a non-negative integer returned by this tool.");

            string defaultComponent = p.Get("componentType");
            var requests = new List<TargetRequest>();
            foreach (JToken token in targetsArray)
            {
                TargetRequest request = ParseTarget(token, defaultComponent);
                if (request == null)
                    return Error("INVALID_PARAMS", "Each target must be a string or an object containing a non-empty 'target'.");
                requests.Add(request);
            }

            var findings = new List<object>();
            foreach (TargetRequest request in requests)
            {
                if (!TryResolve(request.Requested, includeInactive, out UnityEngine.Object target, out string code, out string message))
                    return Error(code, message, new { target = request.Requested });

                List<UnityEngine.Object> serializedTargets = ResolveSerializedTargets(target, request.ComponentType, out string componentError);
                if (componentError != null)
                    return Error("COMPONENT_NOT_FOUND", componentError, new { target = request.Requested, component = request.ComponentType });

                foreach (string requestedProperty in properties)
                {
                    List<UnityEngine.Object> propertyTargets = TargetsForProperty(
                        serializedTargets,
                        requestedProperty,
                        out string propertyPath);
                    bool foundProperty = false;
                    foreach (UnityEngine.Object serializedTarget in propertyTargets)
                    {
                        var serializedObject = new SerializedObject(serializedTarget);
                        serializedObject.UpdateIfRequiredOrScript();
                        SerializedProperty property = serializedObject.FindProperty(propertyPath);
                        if (property == null) continue;

                        foundProperty = true;
                        object finding = BuildFinding(request.Requested, serializedTarget, property, includePrefab);
                        bool brokenReference = property.propertyType == SerializedPropertyType.ObjectReference
                            && property.objectReferenceValue == null
                            && property.objectReferenceEntityIdValue != 0;
                        if (!brokenReference || includeMissing)
                            findings.Add(finding);
                    }
                    if (!foundProperty && propertyTargets.Count > 0)
                        findings.Add(NotFoundFinding(request.Requested, propertyTargets[0], propertyPath));
                }
            }

            var page = findings.Skip(offset).Take(pageSize).ToArray();
            int nextOffset = offset + page.Length;
            string nextCursor = nextOffset < findings.Count ? nextOffset.ToString() : null;
            return new SuccessResponse(
                $"Inspected {page.Length} serialized finding(s).",
                new
                {
                    findings = page,
                    total = findings.Count,
                    next_cursor = nextCursor,
                    page_size = pageSize
                });
        }

        private static TargetRequest ParseTarget(JToken token, string defaultComponent)
        {
            if (token.Type == JTokenType.String)
            {
                string requested = token.Value<string>();
                return string.IsNullOrWhiteSpace(requested) ? null : new TargetRequest
                {
                    Requested = requested,
                    ComponentType = defaultComponent
                };
            }
            if (!(token is JObject obj)) return null;
            string target = obj.Value<string>("target") ?? obj.Value<string>("path") ?? obj.Value<string>("global_object_id");
            return string.IsNullOrWhiteSpace(target) ? null : new TargetRequest
            {
                Requested = target,
                ComponentType = obj.Value<string>("component") ?? defaultComponent
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

            string method = requested.Contains("/") ? "by_path" : "by_name";
            List<int> matches = GameObjectLookup.SearchGameObjects(method, requested, includeInactive, 2);
            if (matches.Count == 0)
            {
                code = "TARGET_NOT_FOUND";
                message = $"Serialized target '{requested}' was not found.";
                return false;
            }
            if (matches.Count > 1)
            {
                code = "TARGET_AMBIGUOUS";
                message = $"Serialized target '{requested}' matched multiple objects; use a hierarchy path or GlobalObjectId.";
                return false;
            }
            target = GameObjectLookup.FindById(matches[0]);
            return target != null;
        }

        private static List<UnityEngine.Object> ResolveSerializedTargets(
            UnityEngine.Object target,
            string componentType,
            out string error)
        {
            error = null;
            if (target is Component directComponent)
            {
                if (!MatchesComponentType(directComponent, componentType))
                {
                    error = $"Resolved component does not match component filter '{componentType}'.";
                    return new List<UnityEngine.Object>();
                }
                return new List<UnityEngine.Object> { directComponent };
            }
            if (!(target is GameObject go))
                return new List<UnityEngine.Object> { target };

            Component[] components = go.GetComponents<Component>().Where(item => item != null).ToArray();
            if (string.IsNullOrWhiteSpace(componentType))
                return components.Cast<UnityEngine.Object>().ToList();

            Type type = GameObjectLookup.FindComponentType(componentType);
            if (type == null)
            {
                error = $"Component type '{componentType}' could not be resolved.";
                return new List<UnityEngine.Object>();
            }
            var matches = components.Where(type.IsInstanceOfType).Cast<UnityEngine.Object>().ToList();
            if (matches.Count == 0)
                error = $"Target '{GameObjectLookup.GetGameObjectPath(go)}' has no component '{componentType}'.";
            return matches;
        }

        private static bool MatchesComponentType(Component component, string componentType)
        {
            if (string.IsNullOrWhiteSpace(componentType)) return true;
            Type type = component.GetType();
            return string.Equals(type.Name, componentType, StringComparison.Ordinal)
                || string.Equals(type.FullName, componentType, StringComparison.Ordinal);
        }

        private static List<UnityEngine.Object> TargetsForProperty(
            List<UnityEngine.Object> serializedTargets,
            string requestedProperty,
            out string propertyPath)
        {
            propertyPath = requestedProperty;
            foreach (UnityEngine.Object target in serializedTargets)
            {
                if (!(target is Component component)) continue;
                Type type = component.GetType();
                string fullPrefix = type.FullName + ".";
                string shortPrefix = type.Name + ".";
                if (requestedProperty.StartsWith(fullPrefix, StringComparison.Ordinal))
                {
                    propertyPath = requestedProperty.Substring(fullPrefix.Length);
                    return new List<UnityEngine.Object> { target };
                }
                if (requestedProperty.StartsWith(shortPrefix, StringComparison.Ordinal))
                {
                    propertyPath = requestedProperty.Substring(shortPrefix.Length);
                    return new List<UnityEngine.Object> { target };
                }
            }
            return serializedTargets;
        }

        private static object BuildFinding(
            string requested,
            UnityEngine.Object serializedTarget,
            SerializedProperty property,
            bool includePrefab)
        {
            bool isObjectReference = property.propertyType == SerializedPropertyType.ObjectReference;
            UnityEngine.Object referenced = isObjectReference ? property.objectReferenceValue : null;
            bool missing = isObjectReference && referenced == null && property.objectReferenceEntityIdValue != 0;
            bool isNull = isObjectReference && referenced == null && !missing;
            GameObject owner = OwnerGameObject(serializedTarget);

            return new
            {
                target = requested,
                path = owner != null ? GameObjectLookup.GetGameObjectPath(owner) : AssetDatabase.GetAssetPath(serializedTarget),
                global_object_id = GlobalIdOf(serializedTarget),
                component = serializedTarget.GetType().FullName,
                property = property.propertyPath,
                found = true,
                value_kind = isObjectReference ? "object_reference" : SerializedKind(property),
                value = isObjectReference ? null : ReadValue(property),
                referenced_object = referenced != null ? referenced.name : null,
                referenced_type = referenced != null ? referenced.GetType().FullName : null,
                referenced_global_object_id = referenced != null ? GlobalIdOf(referenced) : null,
                referenced_asset_path = referenced != null ? AssetDatabase.GetAssetPath(referenced) : null,
                is_null = isNull,
                missing,
                prefab = includePrefab ? PrefabInfo(serializedTarget, property) : null
            };
        }

        private static object NotFoundFinding(string requested, UnityEngine.Object target, string property) => new
        {
            target = requested,
            path = OwnerGameObject(target) != null
                ? GameObjectLookup.GetGameObjectPath(OwnerGameObject(target))
                : AssetDatabase.GetAssetPath(target),
            global_object_id = GlobalIdOf(target),
            component = target.GetType().FullName,
            property,
            found = false,
            value_kind = "property_not_found",
            is_null = false,
            missing = false
        };

        private static object PrefabInfo(UnityEngine.Object target, SerializedProperty property)
        {
            GameObject owner = OwnerGameObject(target);
            bool isInstance = owner != null && PrefabUtility.IsPartOfPrefabInstance(owner);
            UnityEngine.Object source = isInstance ? PrefabUtility.GetCorrespondingObjectFromSource(target) : null;
            string sourceAsset = source != null ? AssetDatabase.GetAssetPath(source) : null;
            return new
            {
                is_instance = isInstance,
                source_asset = sourceAsset,
                source_object = source != null ? source.name : null,
                source_global_object_id = source != null ? GlobalIdOf(source) : null,
                is_override = isInstance && property.prefabOverride
            };
        }

        private static GameObject OwnerGameObject(UnityEngine.Object target)
        {
            if (target is GameObject go) return go;
            return (target as Component)?.gameObject;
        }

        private static string GlobalIdOf(UnityEngine.Object target)
        {
            if (target == null) return null;
            return GlobalObjectId.GetGlobalObjectIdSlow(target).ToString();
        }

        private static string SerializedKind(SerializedProperty property) =>
            property.propertyType.ToString().ToLowerInvariant();

        private static object ReadValue(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer: return property.longValue;
                case SerializedPropertyType.Boolean: return property.boolValue;
                case SerializedPropertyType.Float: return property.doubleValue;
                case SerializedPropertyType.String: return property.stringValue;
                case SerializedPropertyType.Color: return property.colorValue;
                case SerializedPropertyType.LayerMask: return property.intValue;
                case SerializedPropertyType.Enum:
                    return property.enumValueIndex >= 0 && property.enumValueIndex < property.enumDisplayNames.Length
                        ? property.enumDisplayNames[property.enumValueIndex]
                        : property.enumValueIndex.ToString();
                case SerializedPropertyType.Vector2: return property.vector2Value;
                case SerializedPropertyType.Vector3: return property.vector3Value;
                case SerializedPropertyType.Vector4: return property.vector4Value;
                case SerializedPropertyType.Rect: return property.rectValue;
                case SerializedPropertyType.ArraySize: return property.intValue;
                case SerializedPropertyType.Character: return (char)property.intValue;
                case SerializedPropertyType.AnimationCurve: return property.animationCurveValue;
                case SerializedPropertyType.Bounds: return property.boundsValue;
                case SerializedPropertyType.Quaternion: return property.quaternionValue;
                case SerializedPropertyType.Vector2Int: return property.vector2IntValue;
                case SerializedPropertyType.Vector3Int: return property.vector3IntValue;
                case SerializedPropertyType.RectInt: return property.rectIntValue;
                case SerializedPropertyType.BoundsInt: return property.boundsIntValue;
                case SerializedPropertyType.ManagedReference: return property.managedReferenceFullTypename;
                default: return property.hasVisibleChildren ? "<serialized object>" : property.type;
            }
        }

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
