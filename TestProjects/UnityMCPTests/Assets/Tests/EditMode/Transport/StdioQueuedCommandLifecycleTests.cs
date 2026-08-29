using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services.Transport.Transports;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Transport
{
    public class StdioQueuedCommandLifecycleTests
    {
        private static QueuedCommand Command() => new QueuedCommand
        {
            CommandJson = "{}",
            Tcs = new TaskCompletionSource<string>(),
            EnqueuedAtTimestamp = Stopwatch.GetTimestamp()
        };

        [Test]
        public void AbandonedCommand_CannotBeginExecutionFromAnEarlierBatchSnapshot()
        {
            QueuedCommand blockingCommand = Command();
            QueuedCommand laterCommand = Command();
            var queue = new Dictionary<string, QueuedCommand>
            {
                ["blocking"] = blockingCommand,
                ["later"] = laterCommand
            };
            var drainedBatch = StdioBridgeHost.SnapshotWaitingCommands(queue);

            Assert.AreEqual(2, drainedBatch.Count);
            Assert.IsTrue(blockingCommand.IsWaiting,
                "Taking the batch snapshot must not prematurely mark commands as executing.");
            Assert.IsTrue(laterCommand.IsWaiting,
                "Taking the batch snapshot must not prematurely mark commands as executing.");
            Assert.IsTrue(drainedBatch.Single(item => item.id == "blocking").command.TryBeginExecution());

            // Simulate the listener timing out the later command while the first handler blocks.
            Assert.IsTrue(laterCommand.TryAbandon());

            Assert.IsFalse(drainedBatch.Single(item => item.id == "later").command.TryBeginExecution(),
                "A command abandoned after the batch snapshot must never execute later.");
        }

        [Test]
        public void ExecutingCommand_CannotBeAbandonedByClientTimeout()
        {
            QueuedCommand command = Command();

            Assert.IsTrue(command.TryBeginExecution());

            Assert.IsFalse(command.TryAbandon(),
                "A handler that has actually started cannot safely be recalled.");
            Assert.IsTrue(command.IsExecuting);
        }
    }
}
