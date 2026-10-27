using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json;
using UnityEngine;

namespace MCPForUnity.Editor.Services
{
    /// <summary>Opt-in project policy, enforced at the shared MCP/CLI test-job entry point.</summary>
    internal static class TestStageGuard
    {
        private sealed class Configuration
        {
            public string[] Inputs;
        }

        // The job Begin admitted and the state file to settle when it ends. Lost with the domain;
        // the next Begin then settles the job from its record instead.
        private static string _pendingJob;
        private static string _pendingStatePath;

        internal static string PendingJob => _pendingJob;

        internal static void Begin(string jobId, string mode, TestFilterOptions filters)
        {
            string project = Path.GetDirectoryName(Application.dataPath);
            string configPath = Path.Combine(project, "ProjectSettings", "McpTestStages.json");
            if (!File.Exists(configPath)) return;
            Configuration config;
            try { config = JsonConvert.DeserializeObject<Configuration>(File.ReadAllText(configPath)); }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    "test_stage_config_invalid: ProjectSettings/McpTestStages.json is not valid JSON: " + ex.Message);
            }
            if (config?.Inputs == null || config.Inputs.Length == 0 || config.Inputs.Any(string.IsNullOrWhiteSpace))
                throw new InvalidOperationException(
                    "test_stage_config_invalid: ProjectSettings/McpTestStages.json must list non-empty inputs.");

            string statePath = Path.Combine(project, "Library", "McpTestStages.json");
            TestStageState state;
            try
            {
                state = File.Exists(statePath)
                    ? JsonConvert.DeserializeObject<TestStageState>(File.ReadAllText(statePath))
                    : new TestStageState();
            }
            catch (JsonException) { state = null; }
            if (state == null)
                throw new InvalidOperationException("test_stage_state_invalid: " + statePath
                    + " is unreadable. Deleting it resets the recorded focused pass and failed suite.");
            var previous = state.PendingJob == null ? null : TestJobManager.GetJob(state.PendingJob);
            state.Reconcile(Fingerprint(project, config.Inputs), Outcome(previous));
            bool focused = ExplicitTestExclusion.NamesTests(filters);
            string refusal = state.Refusal(focused, mode);
            if (refusal == null)
            {
                state.PendingJob = jobId;
                state.PendingFocused = focused;
                state.PendingMode = mode;
            }
            // Persist reconciliation even on refusal, so clearing/reloading a failed job cannot
            // erase the failure. A missing job is fail-closed, so jobs settle themselves as they
            // end (RecordEnded): only one lost before then, such as to an editor restart, counts.
            McpConfigurationHelper.WriteAtomicFile(statePath, JsonConvert.SerializeObject(state));
            if (refusal != null) throw new InvalidOperationException(refusal);
            _pendingJob = jobId;
            _pendingStatePath = statePath;
        }

        /// <summary>Settles the admitted job's outcome once it has ended.</summary>
        internal static void RecordEnded(TestJob job)
        {
            if (job == null || job.JobId != _pendingJob || job.Status == TestJobStatus.Running) return;
            _pendingJob = null;
            try
            {
                var state = JsonConvert.DeserializeObject<TestStageState>(File.ReadAllText(_pendingStatePath));
                if (state?.PendingJob != job.JobId) return;
                state.Record(Outcome(job));
                McpConfigurationHelper.WriteAtomicFile(_pendingStatePath, JsonConvert.SerializeObject(state));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                McpLog.Warn($"[TestStageGuard] Could not record job {job.JobId}'s outcome; the next run "
                    + $"reads it from the job record instead: {ex.Message}");
            }
        }

        // A job that is gone cannot vouch for anything, so it counts against the stage; one that
        // ended without executing a test (refused, never dispatched, failed to start) counts for neither.
        internal static TestStageOutcome Outcome(TestJob job)
        {
            if (job == null) return TestStageOutcome.Failed;
            if (job.Status == TestJobStatus.Succeeded && job.Summary?.Passed > 0 && job.Summary.Failed == 0)
                return TestStageOutcome.Passed;
            bool executedNothing = job.Status != TestJobStatus.Running && job.CompletedTests == 0
                && (job.Summary == null || job.Summary.Passed + job.Summary.Failed == 0);
            return executedNothing ? TestStageOutcome.NotRun : TestStageOutcome.Failed;
        }

        // Content, paths, additions and deletions all matter. Generated build directories are not
        // inputs. Hashes are deliberately conservative: no dependency-based result skipping yet.
        internal static string Fingerprint(string project, string[] inputs)
        {
            using (var hash = SHA256.Create())
            using (var sink = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write))
            {
                foreach (string input in inputs.OrderBy(p => p, StringComparer.Ordinal))
                {
                    string path = Path.GetFullPath(Path.Combine(project, input));
                    var files = File.Exists(path) ? new[] { path }
                        : Directory.Exists(path) ? EnumerateInputs(path).ToArray()
                        : throw new InvalidOperationException("test_stage_input_missing: '" + input
                            + "' listed in ProjectSettings/McpTestStages.json does not exist (" + path + ").");
                    foreach (string file in files.OrderBy(p => p, StringComparer.Ordinal))
                    {
                        byte[] name = Encoding.UTF8.GetBytes(file);
                        byte[] size = BitConverter.GetBytes(new FileInfo(file).Length);
                        sink.Write(BitConverter.GetBytes(name.Length), 0, sizeof(int));
                        sink.Write(name, 0, name.Length);
                        sink.Write(size, 0, size.Length);
                        using (var source = File.OpenRead(file)) source.CopyTo(sink);
                    }
                }
                sink.FlushFinalBlock();
                return BitConverter.ToString(hash.Hash).Replace("-", "");
            }
        }

        private static System.Collections.Generic.IEnumerable<string> EnumerateInputs(string path)
        {
            foreach (string file in Directory.GetFiles(path)) yield return file;
            foreach (string directory in Directory.GetDirectories(path))
            {
                string name = Path.GetFileName(directory);
                if (name == "bin" || name == "obj" || name == ".git" || name == "node_modules") continue;
                foreach (string file in EnumerateInputs(directory)) yield return file;
            }
        }
    }
}
