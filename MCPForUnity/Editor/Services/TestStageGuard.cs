using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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

        internal static void Begin(string jobId, string mode, TestFilterOptions filters)
        {
            string project = Path.GetDirectoryName(Application.dataPath);
            string configPath = Path.Combine(project, "ProjectSettings", "McpTestStages.json");
            if (!File.Exists(configPath)) return;
            var config = JsonConvert.DeserializeObject<Configuration>(File.ReadAllText(configPath));
            if (config?.Inputs == null || config.Inputs.Length == 0)
                throw new InvalidOperationException("McpTestStages.json must list non-empty inputs.");

            string statePath = Path.Combine(project, "Library", "McpTestStages.json");
            var state = File.Exists(statePath)
                ? JsonConvert.DeserializeObject<TestStageState>(File.ReadAllText(statePath))
                : new TestStageState();
            if (state == null) throw new InvalidOperationException("Invalid test-stage state: " + statePath);
            var previous = state.PendingJob == null ? null : TestJobManager.GetJob(state.PendingJob);
            bool passed = previous?.Status == TestJobStatus.Succeeded
                && previous.Summary?.Passed > 0 && previous.Summary.Failed == 0;
            state.Reconcile(Fingerprint(project, config.Inputs), passed);
            bool focused = HasSelection(filters?.TestNames) || HasSelection(filters?.GroupNames)
                || HasSelection(filters?.CategoryNames);
            string refusal = state.Refusal(focused, mode);
            // Persist reconciliation even on refusal, so clearing/reloading a failed job cannot
            // erase the failure. A missing job is fail-closed; a fresh focus run recovers it.
            Save(statePath, state);
            if (refusal != null) throw new InvalidOperationException(refusal);
            state.PendingJob = jobId;
            state.PendingFocused = focused;
            state.PendingMode = mode;
            Save(statePath, state);
        }

        private static bool HasSelection(string[] values) => values != null
            && values.Any(v => !string.IsNullOrWhiteSpace(v));

        private static void Save(string path, TestStageState state)
        {
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonConvert.SerializeObject(state));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
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
                        : throw new FileNotFoundException("Missing test-stage input", path);
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
