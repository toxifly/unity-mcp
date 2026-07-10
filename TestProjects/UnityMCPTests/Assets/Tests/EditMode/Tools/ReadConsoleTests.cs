using System;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using MCPForUnity.Editor.Tools;
using static MCPForUnityTests.Editor.TestUtilities;

namespace MCPForUnityTests.Editor.Tools
{
    public class ReadConsoleTests
    {
        [Test]
        public void HandleCommand_Clear_Works()
        {
            // Arrange
            // Ensure there's something to clear
            Debug.Log("Log to clear");
            
            // Verify content exists before clear
            var getBefore = ToJObject(ReadConsole.HandleCommand(new JObject { ["action"] = "get", ["types"] = new JArray { "error", "warning", "log" }, ["count"] = 10 }));
            Assert.IsTrue(getBefore.Value<bool>("success"), getBefore.ToString());
            var entriesBefore = getBefore["data"] as JArray;
            
            // Ideally we'd assert count > 0, but other tests/system logs might affect this.
            // Just ensuring the call doesn't fail is a baseline, but let's try to be stricter if possible.
            // Since we just logged, there should be at least one entry.
            Assert.IsTrue(entriesBefore != null && entriesBefore.Count > 0, "Setup failed: console should have logs.");

            // Act
            var result = ToJObject(ReadConsole.HandleCommand(new JObject { ["action"] = "clear" }));

            // Assert
            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            
            // Verify clear effect
            var getAfter = ToJObject(ReadConsole.HandleCommand(new JObject { ["action"] = "get", ["types"] = new JArray { "error", "warning", "log" }, ["count"] = 10 }));
            Assert.IsTrue(getAfter.Value<bool>("success"), getAfter.ToString());
            var entriesAfter = getAfter["data"] as JArray;
            Assert.IsTrue(entriesAfter == null || entriesAfter.Count == 0, "Console should be empty after clear.");
        }

        [Test]
        public void HandleCommand_Get_Works()
        {
            // Arrange
            string uniqueMessage = $"Test Log Message {Guid.NewGuid()}";
            Debug.Log(uniqueMessage);
            
            var paramsObj = new JObject
            {
                ["action"] = "get",
                ["types"] = new JArray { "error", "warning", "log" },
                ["format"] = "detailed",
                ["count"] = 1000 // Fetch enough to likely catch our message
            };

            // Act
            var result = ToJObject(ReadConsole.HandleCommand(paramsObj));

            // Assert
            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            var data = result["data"] as JArray;
            Assert.IsNotNull(data, "Data array should not be null.");
            Assert.IsTrue(data.Count > 0, "Should retrieve at least one log entry.");

            // Verify content
            bool found = false;
            foreach (var entry in data)
            {
                if (entry["message"]?.ToString().Contains(uniqueMessage) == true)
                {
                    found = true;
                    break;
                }
            }
            Assert.IsTrue(found, $"The unique log message '{uniqueMessage}' was not found in retrieved logs.");
        }

        [Test]
        public void HandleCommand_ErrorFilter_ExcludesNormalLogContainingExceptionText()
        {
            string uniqueMessage = $"Ordinary Exception status {Guid.NewGuid()}";
            Debug.Log(uniqueMessage);

            var errors = ToJObject(ReadConsole.HandleCommand(new JObject
            {
                ["action"] = "get",
                ["types"] = new JArray { "error" },
                ["filterText"] = uniqueMessage,
                ["format"] = "detailed",
                ["count"] = 1000
            }));
            var errorEntries = errors["data"] as JArray;
            Assert.IsTrue(errors.Value<bool>("success"), errors.ToString());
            Assert.IsNotNull(errorEntries);
            Assert.AreEqual(0, errorEntries.Count, "A normal Debug.Log must not be promoted by message text.");

            var logs = ToJObject(ReadConsole.HandleCommand(new JObject
            {
                ["action"] = "get",
                ["types"] = new JArray { "log" },
                ["filterText"] = uniqueMessage,
                ["format"] = "detailed",
                ["count"] = 1000
            }));
            var logEntries = logs["data"] as JArray;
            Assert.IsTrue(logs.Value<bool>("success"), logs.ToString());
            Assert.IsNotNull(logEntries);
            Assert.AreEqual(1, logEntries.Count);
            Assert.AreEqual("Log", logEntries[0]["unity_log_type"]?.ToString());
            Assert.AreEqual("user", logEntries[0]["source"]?.ToString());
        }

        [TestCase(1 << 10, LogType.Log)]
        [TestCase((1 << 9) | 1, LogType.Warning)]
        [TestCase((1 << 8) | 1, LogType.Error)]
        [TestCase((1 << 17) | 1 | (1 << 4), LogType.Exception)]
        [TestCase((1 << 21) | (1 << 1), LogType.Assert)]
        [TestCase(1 << 11, LogType.Error)]
        [TestCase(1 << 12, LogType.Warning)]
        public void GetLogTypeFromMode_MapsUnityConsoleFlags(int mode, LogType expected)
        {
            Assert.AreEqual(expected, ReadConsole.GetLogTypeFromMode(mode));
        }

        [Test]
        public void GetLogSource_ClassifiesKnownSources()
        {
            Assert.AreEqual("compiler", ReadConsole.GetLogSource(1 << 11, "error CS1000", "Assets/Test.cs"));
            Assert.AreEqual("test_runner", ReadConsole.GetLogSource(0, "Saving Test Runner results", ""));
            Assert.AreEqual("mcp", ReadConsole.GetLogSource(0, "MCP-FOR-UNITY connected", ""));
            Assert.AreEqual("unity_internal", ReadConsole.GetLogSource(0, "Imported", "Packages/com.unity.test/file.cs"));
            Assert.AreEqual("user", ReadConsole.GetLogSource(0, "Selected roster unit", "Assets/Game.cs"));
        }
    }
}
