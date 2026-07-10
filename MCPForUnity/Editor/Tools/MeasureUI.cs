using System.Collections.Generic;
using System.Text;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Read-only measurement tool for uGUI (Canvas / RectTransform) layouts.
    ///
    /// Returns the bounds of named GameObjects (and, optionally, their immediate
    /// RectTransform children) in two spaces:
    ///   • canvas-local — relative to a reference RectTransform (defaults to the root
    ///     Canvas, so origin is the canvas centre), matching the coordinates you get from
    ///     RectTransform.GetWorldCorners + reference.InverseTransformPoint.
    ///   • screen — pixel coordinates (bottom-left origin, Unity convention).
    ///
    /// This exists so layout work can be verified numerically (clearances, overlaps,
    /// clipping) without capturing a screenshot: measuring a handful of rects costs a
    /// couple hundred tokens and is deterministic, whereas a Game View capture is large
    /// and returns white when the view is unfocused. Use it for the iteration loop; leave
    /// the "does it look right" call to a human eyeballing the live Game View.
    /// </summary>
    [McpForUnityTool("measure_ui")]
    public static class MeasureUI
    {
        public static object HandleCommand(JObject @params)
        {
            if (@params == null)
            {
                return new ErrorResponse("Parameters cannot be null.");
            }

            var p = new ToolParams(@params);
            bool includeInactive = p.GetBool("includeInactive", true);
            bool includeChildren = p.GetBool("includeChildren", false);
            string space = (p.Get("space", "both") ?? "both").ToLowerInvariant();
            bool wantCanvas = space is "canvas" or "both";
            bool wantScreen = space is "screen" or "both";

            var targets = new List<string>();
            var arr = p.GetStringArray("targets");
            if (arr != null) targets.AddRange(arr);
            string container = p.Get("container");
            if (targets.Count == 0 && string.IsNullOrEmpty(container))
            {
                return new ErrorResponse("Provide 'targets' (names/paths) and/or 'container'.");
            }

            // Resolve the reference RectTransform that defines canvas-local space.
            RectTransform reference = ResolveReference(p, targets, container, includeInactive, out GameObject referenceGo);

            var elements = new List<object>();
            var corners = new Vector3[4];

            foreach (var t in targets)
            {
                var go = Resolve(t, includeInactive);
                if (go == null)
                {
                    elements.Add(new { name = t, found = false });
                    continue;
                }
                elements.Add(Measure(go, reference, corners, wantCanvas, wantScreen));
                if (includeChildren)
                {
                    AddChildren(go.transform, reference, corners, wantCanvas, wantScreen, elements);
                }
            }

            if (!string.IsNullOrEmpty(container))
            {
                var cgo = Resolve(container, includeInactive);
                if (cgo == null)
                {
                    return new ErrorResponse($"Container '{container}' not found.");
                }
                elements.Add(Measure(cgo, reference, corners, wantCanvas, wantScreen));
                AddChildren(cgo.transform, reference, corners, wantCanvas, wantScreen, elements);
            }

            object canvasSize = null;
            if (reference != null)
            {
                canvasSize = new { width = reference.rect.width, height = reference.rect.height };
            }

            return new SuccessResponse($"Measured {elements.Count} element(s)", new
            {
                reference = referenceGo != null ? new { name = referenceGo.name, path = PathOf(referenceGo.transform) } : null,
                referenceSize = canvasSize,
                space,
                note = "canvas: reference-local (origin = reference centre, y up). screen: pixels, y up from bottom.",
                elements
            });
        }

        private static void AddChildren(Transform parent, RectTransform reference, Vector3[] corners,
            bool wantCanvas, bool wantScreen, List<object> elements)
        {
            foreach (Transform child in parent)
            {
                if (child is RectTransform)
                {
                    elements.Add(Measure(child.gameObject, reference, corners, wantCanvas, wantScreen));
                }
            }
        }

        private static object Measure(GameObject go, RectTransform reference, Vector3[] corners,
            bool wantCanvas, bool wantScreen)
        {
            var rt = go.transform as RectTransform;
            if (rt == null)
            {
                return new { name = go.name, path = PathOf(go.transform), found = true, active = go.activeInHierarchy, rectTransform = false };
            }

            object canvasRect = null;
            if (wantCanvas && reference != null)
            {
                canvasRect = ToRect(LocalBounds(rt, reference, corners));
            }

            object screenRect = null;
            if (wantScreen)
            {
                var cam = CameraFor(rt);
                screenRect = ToRect(ScreenBounds(rt, cam, corners));
            }

            return new
            {
                name = go.name,
                path = PathOf(rt),
                found = true,
                active = go.activeInHierarchy,
                canvas = canvasRect,
                screen = screenRect
            };
        }

        private static object ToRect(Rect r) => new
        {
            xMin = r.xMin,
            yMin = r.yMin,
            xMax = r.xMax,
            yMax = r.yMax,
            width = r.width,
            height = r.height,
            cx = r.center.x,
            cy = r.center.y
        };

        private static Rect LocalBounds(RectTransform rt, Transform reference, Vector3[] corners)
        {
            rt.GetWorldCorners(corners);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                Vector3 local = reference.InverseTransformPoint(corners[i]);
                minX = Mathf.Min(minX, local.x); maxX = Mathf.Max(maxX, local.x);
                minY = Mathf.Min(minY, local.y); maxY = Mathf.Max(maxY, local.y);
            }
            return Rect.MinMaxRect(minX, minY, maxX, maxY);
        }

        private static Rect ScreenBounds(RectTransform rt, Camera cam, Vector3[] corners)
        {
            rt.GetWorldCorners(corners);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                Vector2 s = RectTransformUtility.WorldToScreenPoint(cam, corners[i]);
                minX = Mathf.Min(minX, s.x); maxX = Mathf.Max(maxX, s.x);
                minY = Mathf.Min(minY, s.y); maxY = Mathf.Max(maxY, s.y);
            }
            return Rect.MinMaxRect(minX, minY, maxX, maxY);
        }

        /// <summary>Camera a canvas renders through; null for Screen Space - Overlay.</summary>
        private static Camera CameraFor(RectTransform rt)
        {
            var canvas = rt.GetComponentInParent<Canvas>();
            if (canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay)
            {
                return null;
            }
            return canvas.worldCamera;
        }

        private static RectTransform ResolveReference(ToolParams p, List<string> targets, string container,
            bool includeInactive, out GameObject referenceGo)
        {
            referenceGo = null;
            string reference = p.Get("reference");
            if (!string.IsNullOrEmpty(reference))
            {
                referenceGo = Resolve(reference, includeInactive);
            }

            // Default: the root Canvas of the first resolvable target/container.
            if (referenceGo == null)
            {
                GameObject anchor = null;
                foreach (var t in targets)
                {
                    anchor = Resolve(t, includeInactive);
                    if (anchor != null) break;
                }
                if (anchor == null && !string.IsNullOrEmpty(container))
                {
                    anchor = Resolve(container, includeInactive);
                }
                if (anchor != null)
                {
                    var canvas = anchor.GetComponentInParent<Canvas>();
                    if (canvas != null) referenceGo = canvas.rootCanvas.gameObject;
                }
            }

            return referenceGo != null ? referenceGo.transform as RectTransform : null;
        }

        /// <summary>Resolve by hierarchy path when the identifier contains '/', else by name.</summary>
        private static GameObject Resolve(string target, bool includeInactive)
        {
            if (string.IsNullOrEmpty(target)) return null;
            string method = target.Contains("/") ? "by_path" : "by_name";
            var go = GameObjectLookup.FindByTarget(target, method, includeInactive);
            if (go == null && method == "by_path")
            {
                // Fall back to the leaf name if the full path didn't resolve.
                int slash = target.LastIndexOf('/');
                string leaf = slash >= 0 ? target.Substring(slash + 1) : target;
                go = GameObjectLookup.FindByTarget(leaf, "by_name", includeInactive);
            }
            return go;
        }

        private static string PathOf(Transform t)
        {
            var sb = new StringBuilder(t.name);
            var cur = t.parent;
            while (cur != null)
            {
                sb.Insert(0, cur.name + "/");
                cur = cur.parent;
            }
            return sb.ToString();
        }
    }
}
