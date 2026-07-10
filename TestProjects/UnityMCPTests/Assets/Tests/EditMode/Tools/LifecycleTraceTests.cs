using System.Linq;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TestNamespace;
using UnityEditor;
using UnityEngine;
using static MCPForUnityTests.Editor.TestUtilities;

namespace MCPForUnityTests.Editor.Tools
{
    public class LifecycleTraceTests
    {
        private GameObject _target;
        private string _sessionId;

        [TearDown]
        public void TearDown()
        {
            if (!string.IsNullOrEmpty(_sessionId))
                LifecycleTrace.HandleCommand(new JObject { ["action"] = "stop", ["sessionId"] = _sessionId });
            if (_target != null) Object.DestroyImmediate(_target);
        }

        [Test]
        public void InactiveTarget_RecordsEnableAndSerializedPropertyChangeInOrder()
        {
            _target = new GameObject("LifecycleTraceTarget");
            var fixture = _target.AddComponent<SerializedInspectionFixture>();
            _target.SetActive(false);
            JObject started = Start(20, "Awake", "OnEnable", "OnDisable", "OnDestroy", "serialized_property_change");
            Assert.IsTrue(started.Value<bool>("success"), started.ToString());
            _sessionId = started["data"].Value<string>("session_id");

            var serialized = new SerializedObject(fixture);
            serialized.FindProperty("label").stringValue = "changed";
            serialized.ApplyModifiedPropertiesWithoutUndo();
            _target.SetActive(true);

            JObject stopped = Stop();
            JArray events = (JArray)stopped["data"]["events"];
            Assert.IsTrue(events.Any(item => item.Value<string>("event") == "OnEnable"), stopped.ToString());
            JToken propertyEvent = events.FirstOrDefault(item => item.Value<string>("event") == "serialized_property_change");
            Assert.IsNotNull(propertyEvent, stopped.ToString());
            Assert.AreEqual("fixture", propertyEvent["changes"][0].Value<string>("before"));
            Assert.AreEqual("changed", propertyEvent["changes"][0].Value<string>("after"));
            CollectionAssert.IsOrdered(events.Select(item => item.Value<long>("sequence")).ToArray());
            Assert.IsNull(_target.GetComponent<LifecycleTraceProbe>());
            Assert.IsFalse(stopped["data"].Value<bool>("instrumentation_attached"));
        }

        [Test]
        public void EventCap_IsExplicitAndStopRemovesInstrumentation()
        {
            _target = new GameObject("LifecycleTraceCappedTarget");
            JObject started = Start(1, "Awake", "OnEnable", "OnDisable", "OnDestroy");
            _sessionId = started["data"].Value<string>("session_id");
            _target.SetActive(false);
            _target.SetActive(true);

            JObject stopped = Stop();
            Assert.IsTrue(stopped["data"].Value<bool>("truncated"), stopped.ToString());
            Assert.LessOrEqual(stopped["data"]["events"].Count(), 1);
            Assert.IsNull(_target.GetComponent<LifecycleTraceProbe>());
        }

        private JObject Start(int maxEvents, params string[] events)
        {
            return ToJObject(LifecycleTrace.HandleCommand(new JObject
            {
                ["action"] = "start",
                ["targets"] = new JArray(_target.name),
                ["events"] = new JArray(events),
                ["propertyWhitelist"] = new JArray(typeof(SerializedInspectionFixture).FullName + ".label"),
                ["maxEvents"] = maxEvents,
                ["timeoutSeconds"] = 60
            }));
        }

        private JObject Stop()
        {
            JObject result = ToJObject(LifecycleTrace.HandleCommand(new JObject
            {
                ["action"] = "stop",
                ["sessionId"] = _sessionId,
                ["limit"] = 100
            }));
            _sessionId = null;
            return result;
        }
    }
}
