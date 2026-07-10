using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>Read-only, single-invocation geometry verification for uGUI RectTransforms.</summary>
    [McpForUnityTool("measure_ui", AutoRegister = true, Group = "core")]
    public static class MeasureUI
    {
        private const float DefaultTolerance = 0.5f;

        private sealed class Measurement
        {
            public string RequestedTarget;
            public GameObject GameObject;
            public Rect Bounds;
            public string Space;
            public string Reference;
            public bool Clipped;

            public object ToResponse() => new
            {
                target = RequestedTarget,
                name = GameObject.name,
                path = PathOf(GameObject.transform),
                active_self = GameObject.activeSelf,
                active_in_hierarchy = GameObject.activeInHierarchy,
                space = Space,
                reference = Reference,
                bounds = ToBounds(Bounds),
                size = new { width = Bounds.width, height = Bounds.height },
                clipped = Clipped
            };
        }

        private sealed class AssertionResult
        {
            [JsonProperty("type")] public string Type;
            [JsonProperty("passed")] public bool Passed;
            [JsonProperty("message")] public string Message;
            [JsonProperty("targets", NullValueHandling = NullValueHandling.Ignore)] public string[] Targets;
            [JsonProperty("actual", NullValueHandling = NullValueHandling.Ignore)] public object Actual;
        }

        public static object HandleCommand(JObject @params)
        {
            if (@params == null)
                return Error("INVALID_PARAMS", "Parameters cannot be null.");

            var p = new ToolParams(@params);
            string space = NormalizeSpace(p.Get("space", "canvas"));
            if (space == null)
                return Error("INVALID_COORDINATE_SPACE", "'space' must be one of: canvas, local, world, screen_pixels.");

            bool includeInactive = p.GetBool("includeInactive", true);
            bool includeChildren = p.GetBool("includeChildren", false);
            var requestedTargets = (p.GetStringArray("targets") ?? Array.Empty<string>()).ToList();
            string containerName = p.Get("container");
            if (requestedTargets.Count == 0 && string.IsNullOrWhiteSpace(containerName))
                return Error("INVALID_PARAMS", "Provide 'targets' and/or 'container'.");

            var assertionToken = p.GetRaw("assertions");
            if (assertionToken != null && assertionToken.Type != JTokenType.Array)
                return Error("INVALID_PARAMS", "'assertions' must be an array.");
            foreach (var assertion in assertionToken as JArray ?? new JArray())
            {
                if (!(assertion is JObject assertionObject))
                    return Error("INVALID_PARAMS", "Each assertion must be an object.");
                foreach (string assertionTarget in ReadAssertionTargets(assertionObject))
                {
                    if (!requestedTargets.Contains(assertionTarget)) requestedTargets.Add(assertionTarget);
                }
            }

            var resolved = new List<(string requested, GameObject go)>();
            foreach (string target in requestedTargets)
            {
                var go = Resolve(target, includeInactive);
                if (go == null)
                    return Error("TARGET_NOT_FOUND", $"UI target '{target}' was not found.", new { target });
                resolved.Add((target, go));
            }

            GameObject container = null;
            if (!string.IsNullOrWhiteSpace(containerName))
            {
                container = Resolve(containerName, includeInactive);
                if (container == null)
                    return Error("TARGET_NOT_FOUND", $"UI container '{containerName}' was not found.", new { target = containerName });
                if (resolved.All(item => item.go != container))
                    resolved.Add((containerName, container));
            }

            foreach (var item in resolved.ToArray())
            {
                if (!includeChildren && item.go != container) continue;
                foreach (Transform child in item.go.transform)
                {
                    if (child is RectTransform && resolved.All(existing => existing.go != child.gameObject))
                        resolved.Add((PathOf(child), child.gameObject));
                }
            }

            string referenceName = p.Get("reference");
            GameObject referenceGo = string.IsNullOrWhiteSpace(referenceName)
                ? DefaultReference(resolved.Select(item => item.go))
                : Resolve(referenceName, includeInactive);

            if (!string.IsNullOrWhiteSpace(referenceName) && referenceGo == null)
                return Error("TARGET_NOT_FOUND", $"Coordinate reference '{referenceName}' was not found.", new { target = referenceName });
            if (space == "local" && string.IsNullOrWhiteSpace(referenceName))
                return Error("COORDINATE_REFERENCE_REQUIRED", "Local-space measurement requires an explicit RectTransform 'reference'.");
            if (space == "canvas" && referenceGo == null)
                return Error("COORDINATE_REFERENCE_REQUIRED", "Canvas-space measurement requires a RectTransform reference or a target under a Canvas.");

            var referenceRect = referenceGo != null ? referenceGo.transform as RectTransform : null;
            if ((space == "canvas" || space == "local") && referenceRect == null)
                return Error("COORDINATE_REFERENCE_REQUIRED", $"Reference '{referenceGo?.name}' does not have a RectTransform.");

            string referencePath = referenceGo != null ? PathOf(referenceGo.transform) : null;
            var measurements = new List<Measurement>();
            foreach (var item in resolved)
            {
                if (!(item.go.transform is RectTransform rt))
                    return Error("TARGET_NOT_RECT_TRANSFORM", $"UI target '{item.requested}' does not have a RectTransform.", new { target = item.requested });
                Rect bounds = MeasureBounds(rt, space, referenceRect);
                measurements.Add(new Measurement
                {
                    RequestedTarget = item.requested,
                    GameObject = item.go,
                    Bounds = bounds,
                    Space = space,
                    Reference = referencePath,
                    Clipped = IsClipped(rt, bounds, space, referenceRect)
                });
            }

            var assertionResults = new List<AssertionResult>();
            foreach (var assertion in assertionToken as JArray ?? new JArray())
            {
                if (!(assertion is JObject assertionObject))
                    return Error("INVALID_PARAMS", "Each assertion must be an object.");
                object assertionError = EvaluateAssertion(assertionObject, measurements, space, out AssertionResult result);
                if (assertionError != null) return assertionError;
                assertionResults.Add(result);
            }

            int failed = assertionResults.Count(result => !result.Passed);
            return new SuccessResponse($"Measured {measurements.Count} UI element(s); {failed} assertion(s) failed.", new
            {
                space,
                reference = referencePath,
                measurements = measurements.Select(measurement => measurement.ToResponse()).ToArray(),
                assertions = assertionResults,
                summary = new
                {
                    measured = measurements.Count,
                    assertions = assertionResults.Count,
                    passed = assertionResults.Count - failed,
                    failed
                }
            });
        }

        private static object EvaluateAssertion(JObject assertion, List<Measurement> measurements, string space, out AssertionResult result)
        {
            result = null;
            string type = assertion.Value<string>("type")?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(type)) return Error("INVALID_PARAMS", "Every assertion requires a 'type'.");

            string[] names = ReadAssertionTargets(assertion);
            if (names.Length == 0) return Error("INVALID_PARAMS", $"Assertion '{type}' requires target(s).");
            var selected = new List<Measurement>();
            foreach (string name in names)
            {
                var measurement = FindMeasurement(measurements, name);
                if (measurement == null) return Error("TARGET_NOT_FOUND", $"Assertion target '{name}' was not measured.", new { target = name });
                selected.Add(measurement);
            }

            float tolerance = assertion.Value<float?>("tolerance") ?? DefaultTolerance;
            bool passed;
            object actual = null;
            switch (type)
            {
                case "inside":
                    RequireCount(type, selected, 2, out object countError);
                    if (countError != null) return countError;
                    passed = Contains(selected[1].Bounds, selected[0].Bounds, tolerance);
                    break;
                case "covers":
                    RequireCount(type, selected, 2, out countError);
                    if (countError != null) return countError;
                    passed = Contains(selected[0].Bounds, selected[1].Bounds, tolerance);
                    break;
                case "matches_bounds":
                    RequireCount(type, selected, 2, out countError);
                    if (countError != null) return countError;
                    passed = BoundsMatch(selected[0].Bounds, selected[1].Bounds, tolerance);
                    break;
                case "no_overlap":
                    RequireCount(type, selected, 2, out countError);
                    if (countError != null) return countError;
                    passed = !Overlaps(selected[0].Bounds, selected[1].Bounds, tolerance);
                    break;
                case "minimum_gap":
                    RequireCount(type, selected, 2, out countError);
                    if (countError != null) return countError;
                    float requiredGap = assertion.Value<float?>("minimum") ?? assertion.Value<float?>("gap") ?? 0f;
                    float gap = Gap(selected[0].Bounds, selected[1].Bounds);
                    actual = new { gap, minimum = requiredGap };
                    passed = gap + tolerance >= requiredGap;
                    break;
                case "on_screen":
                    if (space != "screen_pixels") return Error("COORDINATE_REFERENCE_REQUIRED", "'on_screen' assertions require space='screen_pixels'.");
                    passed = selected.All(item => !item.Clipped);
                    break;
                case "not_clipped":
                    passed = selected.All(item => !item.Clipped);
                    break;
                case "ordered_left_to_right":
                    passed = IsOrdered(selected.Select(item => item.Bounds.center.x));
                    break;
                case "ordered_top_to_bottom":
                    passed = IsOrdered(selected.Select(item => -item.Bounds.center.y));
                    break;
                default:
                    return Error("INVALID_ASSERTION", $"Unsupported assertion type '{type}'.");
            }

            result = new AssertionResult
            {
                Type = type,
                Passed = passed,
                Targets = names,
                Actual = actual,
                Message = passed ? $"{type} passed." : $"{type} failed."
            };
            return null;
        }

        private static string[] ReadAssertionTargets(JObject assertion)
        {
            if (assertion["targets"] is JArray array) return array.Values<string>().Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            var values = new List<string>();
            string target = assertion.Value<string>("target");
            string container = assertion.Value<string>("container");
            if (!string.IsNullOrWhiteSpace(target)) values.Add(target);
            if (!string.IsNullOrWhiteSpace(container)) values.Add(container);
            return values.ToArray();
        }

        private static void RequireCount(string type, List<Measurement> measurements, int count, out object error)
        {
            error = measurements.Count == count ? null : Error("INVALID_PARAMS", $"Assertion '{type}' requires exactly {count} targets.");
        }

        private static Measurement FindMeasurement(IEnumerable<Measurement> measurements, string target) =>
            measurements.FirstOrDefault(item => string.Equals(item.RequestedTarget, target, StringComparison.Ordinal)
                || string.Equals(item.GameObject.name, target, StringComparison.Ordinal)
                || string.Equals(PathOf(item.GameObject.transform), target, StringComparison.Ordinal));

        private static string NormalizeSpace(string value)
        {
            switch (value?.Trim().ToLowerInvariant())
            {
                case "canvas": return "canvas";
                case "local": return "local";
                case "world": return "world";
                case "screen":
                case "screen_pixels": return "screen_pixels";
                default: return null;
            }
        }

        private static Rect MeasureBounds(RectTransform target, string space, RectTransform reference)
        {
            var corners = new Vector3[4];
            target.GetWorldCorners(corners);
            var points = new Vector2[4];
            for (int i = 0; i < corners.Length; i++)
            {
                switch (space)
                {
                    case "canvas":
                    case "local":
                        Vector3 local = reference.InverseTransformPoint(corners[i]);
                        points[i] = new Vector2(local.x, local.y);
                        break;
                    case "screen_pixels":
                        points[i] = RectTransformUtility.WorldToScreenPoint(CameraFor(target), corners[i]);
                        break;
                    default:
                        points[i] = new Vector2(corners[i].x, corners[i].y);
                        break;
                }
            }
            return Rect.MinMaxRect(points.Min(point => point.x), points.Min(point => point.y), points.Max(point => point.x), points.Max(point => point.y));
        }

        private static bool IsClipped(RectTransform target, Rect bounds, string space, RectTransform reference)
        {
            Rect visible;
            if (space == "canvas" || space == "local") visible = reference.rect;
            else if (space == "screen_pixels")
            {
                var canvas = target.GetComponentInParent<Canvas>()?.rootCanvas;
                visible = canvas != null ? canvas.pixelRect : new Rect(0, 0, Screen.width, Screen.height);
            }
            else return false;
            return !Contains(visible, bounds, DefaultTolerance);
        }

        private static bool Contains(Rect outer, Rect inner, float tolerance) =>
            inner.xMin >= outer.xMin - tolerance && inner.yMin >= outer.yMin - tolerance
            && inner.xMax <= outer.xMax + tolerance && inner.yMax <= outer.yMax + tolerance;

        private static bool BoundsMatch(Rect a, Rect b, float tolerance) =>
            Mathf.Abs(a.xMin - b.xMin) <= tolerance && Mathf.Abs(a.yMin - b.yMin) <= tolerance
            && Mathf.Abs(a.xMax - b.xMax) <= tolerance && Mathf.Abs(a.yMax - b.yMax) <= tolerance;

        private static bool Overlaps(Rect a, Rect b, float tolerance) =>
            a.xMin < b.xMax - tolerance && a.xMax > b.xMin + tolerance
            && a.yMin < b.yMax - tolerance && a.yMax > b.yMin + tolerance;

        private static float Gap(Rect a, Rect b)
        {
            float dx = Mathf.Max(0, Mathf.Max(a.xMin - b.xMax, b.xMin - a.xMax));
            float dy = Mathf.Max(0, Mathf.Max(a.yMin - b.yMax, b.yMin - a.yMax));
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        private static bool IsOrdered(IEnumerable<float> values)
        {
            bool first = true;
            float previous = 0;
            foreach (float value in values)
            {
                if (!first && value < previous) return false;
                previous = value;
                first = false;
            }
            return true;
        }

        private static Camera CameraFor(RectTransform target)
        {
            var canvas = target.GetComponentInParent<Canvas>();
            return canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        }

        private static GameObject DefaultReference(IEnumerable<GameObject> targets)
        {
            foreach (var target in targets)
            {
                var canvas = target.GetComponentInParent<Canvas>();
                if (canvas != null) return canvas.rootCanvas.gameObject;
            }
            return null;
        }

        private static GameObject Resolve(string target, bool includeInactive)
        {
            if (string.IsNullOrWhiteSpace(target)) return null;
            string method = target.Contains("/") ? "by_path" : "by_name";
            return GameObjectLookup.FindByTarget(target, method, includeInactive);
        }

        private static object ToBounds(Rect rect) => new
        {
            x_min = rect.xMin,
            y_min = rect.yMin,
            x_max = rect.xMax,
            y_max = rect.yMax
        };

        private static ErrorResponse Error(string code, string message, object details = null) =>
            new ErrorResponse(code, new { message, details });

        private static string PathOf(Transform transform)
        {
            var path = new StringBuilder(transform.name);
            for (Transform parent = transform.parent; parent != null; parent = parent.parent)
                path.Insert(0, parent.name + "/");
            return path.ToString();
        }
    }
}
