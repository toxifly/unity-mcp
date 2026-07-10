using System;
using System.Collections;
using System.Linq;
using System.Reflection;
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
        private const string ReloadStateKey = "MCPForUnity.LifecycleTrace.ReloadState.v1";
        private GameObject _target;
        private string _sessionId;

        [TearDown]
        public void TearDown()
        {
            if (!string.IsNullOrEmpty(_sessionId))
                LifecycleTrace.HandleCommand(new JObject { ["action"] = "stop", ["sessionId"] = _sessionId });
            if (_target != null) UnityEngine.Object.DestroyImmediate(_target);
            SessionState.EraseString(ReloadStateKey);
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

        [Test]
        public void DomainReload_PersistsOrderedTerminalTraceAndRemovesInstrumentation()
        {
            _target = new GameObject("LifecycleTraceReloadTarget");
            JObject started = Start(20, "Awake", "OnEnable", "OnDisable", "OnDestroy");
            Assert.IsTrue(started.Value<bool>("success"), started.ToString());
            _sessionId = started["data"].Value<string>("session_id");
            _target.SetActive(false);
            _target.SetActive(true);

            InvokeLifecycleMethod("BeforeAssemblyReload");
            InvokeLifecycleMethod("RestoreReloadedSessions");

            JObject polled = Poll();
            JArray events = (JArray)polled["data"]["events"];
            Assert.AreEqual("stopped", polled["data"].Value<string>("status"), polled.ToString());
            Assert.AreEqual("domain_reload", polled["data"].Value<string>("stop_reason"), polled.ToString());
            Assert.IsNotEmpty(events, polled.ToString());
            CollectionAssert.IsOrdered(events.Select(item => item.Value<long>("sequence")).ToArray());
            Assert.IsFalse(polled["data"].Value<bool>("instrumentation_attached"));
            Assert.IsNull(_target.GetComponent<LifecycleTraceProbe>());
        }

        [Test]
        public void Timeout_TransitionsSessionAndRemovesInstrumentation()
        {
            _target = new GameObject("LifecycleTraceTimeoutTarget");
            JObject started = Start(20, "Awake", "OnEnable", "OnDisable", "OnDestroy");
            Assert.IsTrue(started.Value<bool>("success"), started.ToString());
            _sessionId = started["data"].Value<string>("session_id");

            ExpireSession(_sessionId);
            InvokeLifecycleMethod("Update");

            JObject polled = Poll();
            Assert.AreEqual("timed_out", polled["data"].Value<string>("status"), polled.ToString());
            Assert.AreEqual("timed_out", polled["data"].Value<string>("stop_reason"), polled.ToString());
            Assert.IsFalse(polled["data"].Value<bool>("instrumentation_attached"));
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

        private JObject Poll()
        {
            return ToJObject(LifecycleTrace.HandleCommand(new JObject
            {
                ["action"] = "poll",
                ["sessionId"] = _sessionId,
                ["limit"] = 100
            }));
        }

        private static void ExpireSession(string sessionId)
        {
            FieldInfo sessionsField = typeof(LifecycleTrace).GetField(
                "Sessions",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(sessionsField, "LifecycleTrace session store was not found.");
            var sessions = sessionsField.GetValue(null) as IDictionary;
            Assert.IsNotNull(sessions, "LifecycleTrace session store is not dictionary-compatible.");
            object session = sessions[sessionId];
            Assert.IsNotNull(session, $"LifecycleTrace session '{sessionId}' was not found.");
            FieldInfo expiresField = session.GetType().GetField(
                "ExpiresUtc",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsNotNull(expiresField, "LifecycleTrace expiry field was not found.");
            expiresField.SetValue(session, DateTime.UtcNow.AddSeconds(-1));
        }

        private static void InvokeLifecycleMethod(string methodName)
        {
            MethodInfo method = typeof(LifecycleTrace).GetMethod(
                methodName,
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(method, $"LifecycleTrace.{methodName} was not found.");
            method.Invoke(null, null);
        }
    }
}
