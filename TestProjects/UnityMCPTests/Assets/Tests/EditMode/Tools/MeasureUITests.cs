using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
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
