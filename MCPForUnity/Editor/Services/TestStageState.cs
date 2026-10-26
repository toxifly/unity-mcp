using System;

namespace MCPForUnity.Editor.Services
{
    // Persisted independently of domain reloads. This is ordering evidence, not a result cache.
    internal sealed class TestStageState
    {
        public string Fingerprint;
        public bool FocusedPassed;
        public string FailedSuite;
        public string PendingJob;
        public bool PendingFocused;
        public string PendingMode;

        public void Reconcile(string fingerprint, bool? passed)
        {
            if (PendingJob != null)
            {
                if (PendingFocused)
                    FocusedPassed = passed == true;
                else if (passed == true && FailedSuite == PendingMode)
                    FailedSuite = null;
                else if (passed != true)
                    FailedSuite = PendingMode;
                PendingJob = null;
            }
            if (!string.Equals(Fingerprint, fingerprint, StringComparison.Ordinal))
                FocusedPassed = false;
            Fingerprint = fingerprint;
        }

        public string Refusal(bool focused, string mode)
        {
            if (focused) return null;
            if (!FocusedPassed)
                return "test_stage_required: Run a focused test_names, group_names or category_names "
                    + "selection successfully for the current inputs before a full suite. "
                    + "Assembly-only selection is a full suite; empty/skipped runs do not qualify.";
            if (FailedSuite != null && FailedSuite != mode)
                return "test_stage_failed: Repair and rerun the failed " + FailedSuite
                    + " suite before starting another full suite. Focused diagnosis remains allowed.";
            return null;
        }
    }
}
