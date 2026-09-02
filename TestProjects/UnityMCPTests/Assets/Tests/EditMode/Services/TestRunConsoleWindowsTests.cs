using MCPForUnity.Editor.Services;
using NUnit.Framework;
using UnityEditor;

namespace MCPForUnityTests.Editor.Services
{
    /// <summary>
    /// Covers the console index bookkeeping behind read_console(exclude_test_runs=true).
    /// Console positions are injected so the assertions never depend on the live console.
    /// </summary>
    public class TestRunConsoleWindowsTests
    {
        private const string StateKey = "MCPForUnity.TestRunConsoleWindowsV2";
        private string _original;

        [SetUp]
        public void SetUp()
        {
            _original = SessionState.GetString(StateKey, string.Empty);
            SessionState.SetString(StateKey, string.Empty);
        }

        [TearDown]
        public void TearDown()
        {
            SessionState.SetString(StateKey, _original);
        }

        [Test]
        public void EntriesLoggedDuringARunAreCovered()
        {
            TestRunConsoleWindows.MarkRunStarted(10);
            TestRunConsoleWindows.MarkRunFinished(15);

            var windows = TestRunConsoleWindows.GetWindows(20);
            Assert.IsFalse(TestRunConsoleWindows.Covers(windows, 9));
            Assert.IsTrue(TestRunConsoleWindows.Covers(windows, 10));
            Assert.IsTrue(TestRunConsoleWindows.Covers(windows, 14));
            Assert.IsFalse(TestRunConsoleWindows.Covers(windows, 15), "end index is exclusive");
            Assert.IsFalse(TestRunConsoleWindows.Covers(windows, 19));
        }

        [Test]
        public void ARunStillInFlightCoversEverythingLoggedSoFar()
        {
            TestRunConsoleWindows.MarkRunStarted(4);

            var windows = TestRunConsoleWindows.GetWindows(9);
            Assert.IsFalse(TestRunConsoleWindows.Covers(windows, 3));
            Assert.IsTrue(TestRunConsoleWindows.Covers(windows, 8));
        }

        [Test]
        public void ARunThatLoggedNothingRecordsNoWindow()
        {
            TestRunConsoleWindows.MarkRunStarted(7);
            TestRunConsoleWindows.MarkRunFinished(7);

            var windows = TestRunConsoleWindows.GetWindows(7);
            Assert.IsEmpty(windows);
        }

        [Test]
        public void ClearingTheConsoleDropsWindowsThatNoLongerPointAnywhere()
        {
            TestRunConsoleWindows.MarkRunStarted(10);
            TestRunConsoleWindows.MarkRunFinished(15);

            // The console was cleared, so index 12 is now some unrelated new entry.
            var windows = TestRunConsoleWindows.GetWindows(0);
            Assert.IsEmpty(windows);
            Assert.IsFalse(TestRunConsoleWindows.Covers(TestRunConsoleWindows.GetWindows(20), 12));
        }

        [Test]
        public void ReplacingConsoleAtSameOrHigherCountDropsStaleWindows()
        {
            TestRunConsoleWindows.MarkRunStarted(10, "generation-a");
            TestRunConsoleWindows.MarkRunFinished(15, "generation-a");

            // The console was cleared and immediately filled past its old size. Only the
            // first-entry identity reveals that the old index window is no longer meaningful.
            var windows = TestRunConsoleWindows.GetWindows(20, "generation-b");
            Assert.IsEmpty(windows);
        }

        [Test]
        public void ClearSignalDropsWindowsEvenAfterImmediateRepopulation()
        {
            TestRunConsoleWindows.MarkRunStarted(10, "generation-a");
            TestRunConsoleWindows.MarkRunFinished(15, "generation-a");

            TestRunConsoleWindows.NoteConsoleCleared();

            Assert.IsEmpty(TestRunConsoleWindows.GetWindows(20, "generation-b"));
        }

        [Test]
        public void ClearOnPlayRebasesTheRunThatIsStillInFlight()
        {
            // PlayMode runs start, then Unity clears the console as play begins; the run's own
            // entries begin again from zero and must still be covered.
            TestRunConsoleWindows.MarkRunStarted(30);
            TestRunConsoleWindows.MarkRunFinished(5);

            var windows = TestRunConsoleWindows.GetWindows(8);
            Assert.IsTrue(TestRunConsoleWindows.Covers(windows, 0));
            Assert.IsTrue(TestRunConsoleWindows.Covers(windows, 4));
            Assert.IsFalse(TestRunConsoleWindows.Covers(windows, 5));
        }

        [Test]
        public void OnlyTheMostRecentRunsAreKept()
        {
            for (int i = 0; i < 14; i++)
            {
                TestRunConsoleWindows.MarkRunStarted(i * 10);
                TestRunConsoleWindows.MarkRunFinished(i * 10 + 5);
            }

            var windows = TestRunConsoleWindows.GetWindows(200);
            Assert.AreEqual(10, windows.Count);
            Assert.IsFalse(TestRunConsoleWindows.Covers(windows, 32), "run 3 should have aged out");
            Assert.IsTrue(TestRunConsoleWindows.Covers(windows, 132), "run 13 is still tracked");
        }

        [Test]
        public void CorruptStateFallsBackToTrackingNothing()
        {
            SessionState.SetString(StateKey, "not json");
            Assert.IsEmpty(TestRunConsoleWindows.GetWindows(5));
        }
    }
}
