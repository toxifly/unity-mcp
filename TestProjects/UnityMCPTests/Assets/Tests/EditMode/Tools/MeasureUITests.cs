using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using static MCPForUnityTests.Editor.TestUtilities;

namespace MCPForUnityTests.Editor.Tools
{
    public class MeasureUITests
    {
        private GameObject _canvasObject;
        private RectTransform _canvasRect;

        [SetUp]
        public void SetUp()
        {
            _canvasObject = new GameObject("MeasureCanvas", typeof(RectTransform), typeof(Canvas));
            _canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            _canvasRect = _canvasObject.GetComponent<RectTransform>();
            Canvas.ForceUpdateCanvases();
        }

        [TearDown]
        public void TearDown()
        {
            if (_canvasObject != null) Object.DestroyImmediate(_canvasObject);
        }

        [Test]
        public void FullScreenOverlayChild_MatchesCanvasBounds()
        {
            var child = CreateChild("FullScreen", Vector2.zero, Vector2.one, Vector2.zero, false);

            JObject result = ToJObject(MeasureUI.HandleCommand(new JObject
            {
                ["targets"] = new JArray(child.name, _canvasObject.name),
                ["space"] = "canvas",
                ["assertions"] = new JArray(new JObject
                {
                    ["type"] = "matches_bounds",
                    ["targets"] = new JArray(child.name, _canvasObject.name)
                })
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual("canvas", result["data"].Value<string>("space"));
            Assert.IsTrue(result["data"]["assertions"][0].Value<bool>("passed"), result.ToString());

            JObject childBounds = (JObject)result["data"]["measurements"][0]["bounds"];
            JObject canvasBounds = (JObject)result["data"]["measurements"][1]["bounds"];
            Assert.AreEqual(canvasBounds.Value<float>("x_min"), childBounds.Value<float>("x_min"), 0.01f);
            Assert.AreEqual(canvasBounds.Value<float>("x_max"), childBounds.Value<float>("x_max"), 0.01f);
        }

        [Test]
        public void InactiveRectTransform_IsMeasuredByDefault()
        {
            var child = CreateChild("InactiveTarget", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(120, 40), true);

            JObject result = ToJObject(MeasureUI.HandleCommand(new JObject
            {
                ["targets"] = new JArray(child.name),
                ["space"] = "canvas"
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            JToken measurement = result["data"]["measurements"][0];
            Assert.IsFalse(measurement.Value<bool>("active_self"));
            Assert.AreEqual(120f, measurement["size"].Value<float>("width"), 0.01f);
            Assert.AreEqual(40f, measurement["size"].Value<float>("height"), 0.01f);
        }

        [Test]
        public void Assertions_AreEvaluatedTogetherInDeclaredSpace()
        {
            CreateChild("Left", new Vector2(0.25f, 0.5f), new Vector2(0.25f, 0.5f), new Vector2(40, 40), false);
            CreateChild("Right", new Vector2(0.75f, 0.5f), new Vector2(0.75f, 0.5f), new Vector2(40, 40), false);

            JObject result = ToJObject(MeasureUI.HandleCommand(new JObject
            {
                ["targets"] = new JArray("Left", "Right"),
                ["space"] = "canvas",
                ["assertions"] = new JArray(
                    new JObject { ["type"] = "no_overlap", ["targets"] = new JArray("Left", "Right") },
                    new JObject { ["type"] = "ordered_left_to_right", ["targets"] = new JArray("Left", "Right") },
                    new JObject { ["type"] = "inside", ["target"] = "Left", ["container"] = _canvasObject.name })
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual(3, result["data"]["summary"].Value<int>("passed"), result.ToString());
            Assert.AreEqual(0, result["data"]["summary"].Value<int>("failed"), result.ToString());
        }

        [Test]
        public void LocalSpaceWithoutReference_IsRejected()
        {
            var child = CreateChild("NeedsReference", Vector2.zero, Vector2.one, Vector2.zero, false);

            JObject result = ToJObject(MeasureUI.HandleCommand(new JObject
            {
                ["targets"] = new JArray(child.name),
                ["space"] = "local"
            }));

            Assert.IsFalse(result.Value<bool>("success"));
            Assert.AreEqual("COORDINATE_REFERENCE_REQUIRED", result.Value<string>("code"));
        }

        [Test]
        public void OnScreenAssertion_RejectsCanvasCoordinates()
        {
            var child = CreateChild("ScreenCheck", Vector2.zero, Vector2.one, Vector2.zero, false);

            JObject result = ToJObject(MeasureUI.HandleCommand(new JObject
            {
                ["targets"] = new JArray(child.name),
                ["space"] = "canvas",
                ["assertions"] = new JArray(new JObject
                {
                    ["type"] = "on_screen",
                    ["target"] = child.name
                })
            }));

            Assert.IsFalse(result.Value<bool>("success"));
            Assert.AreEqual("COORDINATE_REFERENCE_REQUIRED", result.Value<string>("code"));
        }

        [Test]
        public void PrefabPath_MeasuresOnlyTheHeadlesslyLoadedPrefabHierarchy()
        {
            const string folder = "Assets/Temp/MeasureUIPrefabTests";
            const string prefabPath = folder + "/Measured.prefab";
            EnsureFolder(folder);

            var sceneDuplicate = CreateChild("PrefabOnlyTarget", new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), new Vector2(999, 40), false);
            var prefabRoot = new GameObject("MeasuredRoot", typeof(RectTransform));
            var prefabTarget = new GameObject(sceneDuplicate.name, typeof(RectTransform));
            var prefabRect = prefabTarget.GetComponent<RectTransform>();
            prefabRect.SetParent(prefabRoot.transform, false);
            prefabRect.anchorMin = new Vector2(0.5f, 0.5f);
            prefabRect.anchorMax = new Vector2(0.5f, 0.5f);
            prefabRect.sizeDelta = new Vector2(123, 45);
            PrefabUtility.SaveAsPrefabAsset(prefabRoot, prefabPath);
            Object.DestroyImmediate(prefabRoot);

            try
            {
                JObject result = ToJObject(MeasureUI.HandleCommand(new JObject
                {
                    ["prefabPath"] = prefabPath,
                    ["targets"] = new JArray("PrefabOnlyTarget"),
                    ["includeInactive"] = false,
                    ["space"] = "canvas"
                }));

                Assert.IsTrue(result.Value<bool>("success"), result.ToString());
                Assert.AreEqual(prefabPath, result["data"].Value<string>("prefab_path"));
                Assert.AreEqual("Measured", result["data"].Value<string>("reference"));
                Assert.AreEqual(123f,
                    result["data"]["measurements"][0]["size"].Value<float>("width"), 0.01f);
            }
            finally
            {
                SafeDeleteAsset(prefabPath);
                SafeDeleteAsset(folder);
            }
        }

        [Test]
        public void PrefabPath_RejectsNonPrefabAssets()
        {
            JObject result = ToJObject(MeasureUI.HandleCommand(new JObject
            {
                ["prefabPath"] = "Assets/NotAPrefab.asset",
                ["targets"] = new JArray("Target"),
                ["space"] = "world"
            }));

            Assert.IsFalse(result.Value<bool>("success"));
            Assert.AreEqual("INVALID_ASSET_PATH", result.Value<string>("code"));
        }

        [Test]
        public void PrefabPath_RebuildsLayoutDrivenRectsBeforeMeasuring()
        {
            const string folder = "Assets/Temp/MeasureUILayoutPrefabTests";
            const string prefabPath = folder + "/LayoutDriven.prefab";
            EnsureFolder(folder);
            var prefabRoot = new GameObject("LayoutDriven", typeof(RectTransform));
            prefabRoot.GetComponent<RectTransform>().sizeDelta = new Vector2(200, 200);
            System.Type layoutType = System.Type.GetType("UnityEngine.UI.VerticalLayoutGroup, UnityEngine.UI");
            Assert.IsNotNull(layoutType, "The uGUI VerticalLayoutGroup type should be available in this test project.");
            Component layout = prefabRoot.AddComponent(layoutType);
            layoutType.GetProperty("spacing")?.SetValue(layout, 10f);
            layoutType.GetProperty("childControlHeight")?.SetValue(layout, false);
            layoutType.GetProperty("childForceExpandHeight")?.SetValue(layout, false);

            foreach (string childName in new[] { "First", "Second" })
            {
                var child = new GameObject(childName, typeof(RectTransform));
                child.transform.SetParent(prefabRoot.transform, false);
                child.GetComponent<RectTransform>().sizeDelta = new Vector2(100, 30);
            }
            PrefabUtility.SaveAsPrefabAsset(prefabRoot, prefabPath);
            Object.DestroyImmediate(prefabRoot);

            try
            {
                JObject result = ToJObject(MeasureUI.HandleCommand(new JObject
                {
                    ["prefabPath"] = prefabPath,
                    ["targets"] = new JArray("First", "Second"),
                    ["space"] = "canvas",
                    ["assertions"] = new JArray(
                        new JObject { ["type"] = "no_overlap", ["targets"] = new JArray("First", "Second") },
                        new JObject { ["type"] = "ordered_top_to_bottom", ["targets"] = new JArray("First", "Second") })
                }));

                Assert.IsTrue(result.Value<bool>("success"), result.ToString());
                Assert.AreEqual(2, result["data"]["summary"].Value<int>("passed"), result.ToString());
            }
            finally
            {
                SafeDeleteAsset(prefabPath);
                SafeDeleteAsset(folder);
            }
        }

        [Test]
        public void PrefabPath_RejectsScreenDependentMeasurement()
        {
            const string folder = "Assets/Temp/MeasureUIScreenPrefabTests";
            const string prefabPath = folder + "/Widget.prefab";
            EnsureFolder(folder);
            var prefabRoot = new GameObject("Widget", typeof(RectTransform));
            PrefabUtility.SaveAsPrefabAsset(prefabRoot, prefabPath);
            Object.DestroyImmediate(prefabRoot);

            try
            {
                JObject screenResult = ToJObject(MeasureUI.HandleCommand(new JObject
                {
                    ["prefabPath"] = prefabPath,
                    ["targets"] = new JArray("Widget"),
                    ["space"] = "screen_pixels"
                }));
                Assert.IsFalse(screenResult.Value<bool>("success"));
                Assert.AreEqual("UNSUPPORTED_COORDINATE_SPACE", screenResult.Value<string>("code"));

                JObject assertionResult = ToJObject(MeasureUI.HandleCommand(new JObject
                {
                    ["prefabPath"] = prefabPath,
                    ["targets"] = new JArray("Widget"),
                    ["space"] = "canvas",
                    ["assertions"] = new JArray(new JObject
                    {
                        ["type"] = "on_screen",
                        ["target"] = "Widget"
                    })
                }));
                Assert.IsFalse(assertionResult.Value<bool>("success"));
                Assert.AreEqual("UNSUPPORTED_ASSERTION", assertionResult.Value<string>("code"));

                JObject clippingResult = ToJObject(MeasureUI.HandleCommand(new JObject
                {
                    ["prefabPath"] = prefabPath,
                    ["targets"] = new JArray("Widget"),
                    ["space"] = "canvas",
                    ["assertions"] = new JArray(new JObject
                    {
                        ["type"] = "not_clipped",
                        ["target"] = "Widget"
                    })
                }));
                Assert.IsFalse(clippingResult.Value<bool>("success"));
                Assert.AreEqual("UNSUPPORTED_ASSERTION", clippingResult.Value<string>("code"));
            }
            finally
            {
                SafeDeleteAsset(prefabPath);
                SafeDeleteAsset(folder);
            }
        }

        [Test]
        public void PrefabPath_RejectsAmbiguousNames()
        {
            const string folder = "Assets/Temp/MeasureUIAmbiguousPrefabTests";
            const string prefabPath = folder + "/Ambiguous.prefab";
            EnsureFolder(folder);
            var prefabRoot = new GameObject("Ambiguous", typeof(RectTransform));
            for (int i = 0; i < 2; i++)
            {
                var child = new GameObject("Duplicate", typeof(RectTransform));
                child.transform.SetParent(prefabRoot.transform, false);
            }
            PrefabUtility.SaveAsPrefabAsset(prefabRoot, prefabPath);
            Object.DestroyImmediate(prefabRoot);

            try
            {
                JObject result = ToJObject(MeasureUI.HandleCommand(new JObject
                {
                    ["prefabPath"] = prefabPath,
                    ["targets"] = new JArray("Duplicate"),
                    ["space"] = "canvas"
                }));

                Assert.IsFalse(result.Value<bool>("success"));
                Assert.AreEqual("TARGET_AMBIGUOUS", result.Value<string>("code"));
            }
            finally
            {
                SafeDeleteAsset(prefabPath);
                SafeDeleteAsset(folder);
            }
        }

        private RectTransform CreateChild(string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 size, bool inactive)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(_canvasRect, false);
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.sizeDelta = size;
            rect.anchoredPosition = Vector2.zero;
            if (inactive) go.SetActive(false);
            Canvas.ForceUpdateCanvases();
            return rect;
        }
    }
}
