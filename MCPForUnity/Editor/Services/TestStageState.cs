using System;

namespace MCPForUnity.Editor.Services
{
    // NotRun: the job ended without executing a test (refused, never dispatched, failed to start),
    // so it is evidence of nothing and leaves the recorded stage untouched.
    internal enum TestStageOutcome
    {
        Passed,
        Failed,
        NotRun
    }

    // Persisted independently of domain reloads. This is ordering evidence, not a result cache.
    internal sealed class TestStageState
    {
        public string Fingerprint;
        public bool FocusedPassed;
        public string FailedSuite;
        public string PendingJob;
        public bool PendingFocused;
        public string PendingMode;

        public void Reconcile(string fingerprint, TestStageOutcome outcome)
        {
            Record(outcome);
            if (!string.Equals(Fingerprint, fingerprint, StringComparison.Ordinal))
                FocusedPassed = false;
            Fingerprint = fingerprint;
        }

        // Settles the pending job with its outcome; a settled state ignores later outcomes.
        public void Record(TestStageOutcome outcome)
        {
            if (PendingJob != null && outcome != TestStageOutcome.NotRun)
            {
                if (PendingFocused)
                    FocusedPassed = outcome == TestStageOutcome.Passed;
                else if (outcome == TestStageOutcome.Passed && FailedSuite == PendingMode)
                    FailedSuite = null;
                else if (outcome != TestStageOutcome.Passed)
                    FailedSuite = PendingMode;
            }
            PendingJob = null;
        }

        public string Refusal(bool focused, string mode)
        {
            if (focused) return null;
            if (!FocusedPassed)
                return "test_stage_required: Run a focused test_names, group_names or category_names "
                    + "selection successfully for the current inputs before a full suite. "
                    + "Assembly-only and exclusion-only selections are full suites; empty/skipped runs do not qualify.";
            if (FailedSuite != null && FailedSuite != mode)
                return "test_stage_failed: Repair and rerun the failed " + FailedSuite
                    + " suite before starting another full suite. Focused diagnosis remains allowed.";
            return null;
        }
    }
}
