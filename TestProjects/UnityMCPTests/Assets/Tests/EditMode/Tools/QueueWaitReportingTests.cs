using System;
using System.Diagnostics;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services.Transport;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Tools
{
    /// <summary>
    /// A command that sat behind a domain reload used to come back looking like a slow tool. The
    /// envelope now says how long it waited and what held the main thread, but only when the wait
    /// is long enough to be worth a caller's attention.
    /// </summary>
    [TestFixture]
    public class QueueWaitReportingTests
    {
        [Test]
        public void APromptCommandReportsNoQueueWaitAtAll()
        {
            Assert.IsNull(TransportCommandDispatcher.DescribeQueueWait(DateTime.UtcNow, "manage_scene"));
        }

        [Test]
        public void ALongWaitIsReportedWithItsDurationAndAReason()
        {
            object described = TransportCommandDispatcher.DescribeQueueWait(
                DateTime.UtcNow.AddSeconds(-10), "manage_scene");

            Assert.IsNotNull(described);
            JObject payload = JObject.FromObject(described);
            Assert.GreaterOrEqual(payload.Value<long>("waited_ms"), 10000);
            Assert.IsNotEmpty(payload.Value<string>("reason"));
        }

        [Test]
        public void QueueWaitSnapshotDoesNotIncludeLaterCommandExecutionTime()
        {
            DateTime queuedAtUtc = DateTime.UtcNow.AddSeconds(-10);
            DateTime processingStartedAtUtc = queuedAtUtc.AddMilliseconds(100);

            JObject captured = TransportCommandDispatcher.CaptureQueueWait(
                queuedAtUtc,
                processingStartedAtUtc,
                "main_thread_busy");

            Assert.IsNull(captured,
                "A command that began promptly must not report its later execution duration as queue wait.");
        }

        [Test]
        public void QueueWaitSnapshotPreservesTheReasonObservedAtDispatchStart()
        {
            DateTime processingStartedAtUtc = DateTime.UtcNow;

            JObject captured = TransportCommandDispatcher.CaptureQueueWait(
                processingStartedAtUtc.AddSeconds(-10),
                processingStartedAtUtc,
                "compiling");

            Assert.AreEqual("compiling", captured.Value<string>("reason"));
        }

        [Test]
        public void DeferredPlayModeRecompileIsReportedAsPlayModeTransition()
        {
            string reason = TransportCommandDispatcher.ClassifyQueueWaitReason(
                isActuallyCompiling: false,
                isUpdating: false,
                isPlayingOrWillChangePlaymode: true,
                isApplicationActive: true);

            Assert.AreEqual("play_mode_transition", reason,
                "A raw compilation flag deferred until Play Mode exits must not be classified as active compilation.");
        }

        [TestCase(true, true, true, false, "compiling")]
        [TestCase(false, true, true, false, "asset_import")]
        [TestCase(false, false, true, false, "play_mode_transition")]
        [TestCase(false, false, false, false, "editor_unfocused")]
        [TestCase(false, false, false, true, "main_thread_busy")]
        public void QueueWaitReasonPreservesStatePrecedence(
            bool isActuallyCompiling,
            bool isUpdating,
            bool isPlayingOrWillChangePlaymode,
            bool isApplicationActive,
            string expected)
        {
            Assert.AreEqual(expected, TransportCommandDispatcher.ClassifyQueueWaitReason(
                isActuallyCompiling,
                isUpdating,
                isPlayingOrWillChangePlaymode,
                isApplicationActive));
        }

        [Test]
        public void MonotonicEnqueueTimestampIncludesAnUpstreamStdioWait()
        {
            long processingStartedAt = Stopwatch.GetTimestamp();
            long queuedAt = processingStartedAt - (10L * Stopwatch.Frequency);

            JObject captured = TransportCommandDispatcher.CaptureQueueWait(
                queuedAt,
                processingStartedAt,
                "main_thread_busy");

            Assert.AreEqual(10000, captured.Value<long>("waited_ms"),
                "The dispatcher must retain time already spent in the stdio host queue.");
        }

        [Test]
        public void MonotonicQueueWaitDoesNotDependOnWallClockChanges()
        {
            long processingStartedAt = Stopwatch.GetTimestamp();
            long queuedAt = processingStartedAt - (Stopwatch.Frequency / 10);

            JObject captured = TransportCommandDispatcher.CaptureQueueWait(
                queuedAt,
                processingStartedAt,
                "main_thread_busy");

            Assert.IsNull(captured);
        }

        [Test]
        public async Task AsyncHandlerResponsePreservesLongQueueWaitSnapshot()
        {
            var completion = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            long processingStartedAt = Stopwatch.GetTimestamp();
            long queuedAt = processingStartedAt - (3L * Stopwatch.Frequency);
            JObject queueWait = TransportCommandDispatcher.CaptureQueueWait(
                queuedAt,
                processingStartedAt,
                "main_thread_busy");
            Assert.IsNotNull(queueWait, "The simulated wait must exceed the reporting threshold.");

            object immediateResult = CommandRegistry.ExecuteCommand(
                "batch_execute",
                new JObject(),
                completion,
                queueWait);

            Assert.IsNull(immediateResult, "The fixture command must exercise async completion.");
            JObject response = JObject.Parse(await completion.Task);
            Assert.AreEqual("success", response.Value<string>("status"));
            Assert.AreEqual(3000, response["queue"].Value<long>("waited_ms"));
            Assert.AreEqual("main_thread_busy", response["queue"].Value<string>("reason"));
        }

        [Test]
        public async Task PromptAsyncHandlerResponseHasNullQueueMetadata()
        {
            var completion = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            long processingStartedAt = Stopwatch.GetTimestamp();
            long queuedAt = processingStartedAt - (Stopwatch.Frequency / 10);
            JObject queueWait = TransportCommandDispatcher.CaptureQueueWait(
                queuedAt,
                processingStartedAt,
                "main_thread_busy");
            Assert.IsNull(queueWait, "The simulated wait must remain below the reporting threshold.");

            object immediateResult = CommandRegistry.ExecuteCommand(
                "batch_execute",
                new JObject(),
                completion,
                queueWait);

            Assert.IsNull(immediateResult, "The fixture command must exercise async completion.");
            JObject response = JObject.Parse(await completion.Task);
            Assert.AreEqual(JTokenType.Null, response["queue"]?.Type);
        }
    }
}
