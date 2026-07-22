using System;
using System.Collections;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using MCPForUnity.Editor.Helpers;

namespace MCPForUnityTests.Editor.Tools
{
    /// <summary>
    /// Tests for RunTests tool functionality.
    /// Note: We cannot easily test the full HandleCommand because it would create
    /// recursive test runner calls.
    /// </summary>
    public class RunTestsTests
    {
        [Test]
        public void HandleCommand_WhenTestsAlreadyRunning_ReturnsBusyError()
        {
            // Arrange: Force TestJobManager into a "busy" state without starting a real run.
            // We do this via reflection because TestJobManager is internal.
            var asm = typeof(MCPForUnity.Editor.Services.MCPServiceLocator).Assembly;
            var testJobManagerType = asm.GetType("MCPForUnity.Editor.Services.TestJobManager");
            Assert.NotNull(testJobManagerType, "Could not locate TestJobManager type via reflection");

            var currentJobIdField = testJobManagerType.GetField("_currentJobId", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(currentJobIdField, "Could not locate TestJobManager._currentJobId field");
            var jobsField = testJobManagerType.GetField("Jobs", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(jobsField, "Could not locate TestJobManager.Jobs field");
            var jobs = jobsField.GetValue(null) as IDictionary;
            Assert.NotNull(jobs, "Could not access TestJobManager.Jobs dictionary");

            var testJobType = asm.GetType("MCPForUnity.Editor.Services.TestJob");
            Assert.NotNull(testJobType, "Could not locate TestJob type via reflection");
            object busyJob = Activator.CreateInstance(testJobType, true);
            testJobType.GetProperty("JobId")?.SetValue(busyJob, "busy-test-job-id");
            testJobType.GetProperty("RequestToken")?.SetValue(busyJob, "busy-owner-token");

            var originalJobId = currentJobIdField.GetValue(null) as string;
            object originalJob = jobs.Contains("busy-test-job-id") ? jobs["busy-test-job-id"] : null;
            jobs["busy-test-job-id"] = busyJob;
            currentJobIdField.SetValue(null, "busy-test-job-id");

            try
            {
                var resultObj = MCPForUnity.Editor.Tools.RunTests.HandleCommand(new JObject
                {
                    ["requestToken"] = "new-caller-token"
                }).GetAwaiter().GetResult();

                Assert.IsInstanceOf<ErrorResponse>(resultObj);
                var err = (ErrorResponse)resultObj;
                Assert.AreEqual(false, err.Success);
                Assert.AreEqual("tests_running", err.Code);

                var data = err.Data != null ? JObject.FromObject(err.Data) : null;
                Assert.NotNull(data, "Expected data payload on tests_running error");
                Assert.AreEqual("tests_running", data["reason"]?.ToString());
                Assert.GreaterOrEqual(data["retry_after_ms"]?.Value<int>() ?? 0, 500);
                Assert.AreEqual("busy-test-job-id", data["job_id"]?.ToString(),
                    "the active job id must be surfaced so callers can adopt their own lost-reply run");
                Assert.AreEqual("busy-owner-token", data["request_token"]?.ToString(),
                    "the active job's token, not the retrying caller's token, must establish ownership");
            }
            finally
            {
                currentJobIdField.SetValue(null, originalJobId);
                if (originalJob != null)
                    jobs["busy-test-job-id"] = originalJob;
                else
                    jobs.Remove("busy-test-job-id");
            }
        }

        [Test]
        public void HandleCommand_WithInvalidMode_ReturnsError()
        {
            var resultObj = MCPForUnity.Editor.Tools.RunTests.HandleCommand(new JObject
            {
                ["mode"] = "NotARealMode"
            }).GetAwaiter().GetResult();

            Assert.IsInstanceOf<ErrorResponse>(resultObj);
            var err = (ErrorResponse)resultObj;
            Assert.AreEqual(false, err.Success);
            Assert.IsTrue(err.Error.Contains("Unknown test mode", StringComparison.OrdinalIgnoreCase));
        }
    }
}
