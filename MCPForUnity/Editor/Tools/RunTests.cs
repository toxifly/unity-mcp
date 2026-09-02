using System;
using System.Linq;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Resources.Tests;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Starts a Unity Test Runner run asynchronously and returns a job id immediately.
    /// Use get_test_job(job_id) to poll status/results.
    /// </summary>
    [McpForUnityTool("run_tests", AutoRegister = false, Group = "testing")]
    public static class RunTests
    {
        public static Task<object> HandleCommand(JObject @params)
        {
            try
            {
                // Check for clear_stuck action first
                if (ParamCoercion.CoerceBool(@params?["clear_stuck"], false))
                {
                    bool wasCleared = TestJobManager.ClearStuckJob();
                    return Task.FromResult<object>(new SuccessResponse(
                        wasCleared ? "Stuck job cleared." : "No running job to clear.",
                        new { cleared = wasCleared }
                    ));
                }

                var compileRefusal = CompileErrorRefusal(
                    EditorUtility.scriptCompilationFailed,
                    CompilationStateTracker.LastErrors,
                    CompilationStateTracker.LastErrorDetails);
                if (compileRefusal != null)
                {
                    return Task.FromResult(compileRefusal);
                }

                string modeStr = @params?["mode"]?.ToString();
                if (string.IsNullOrWhiteSpace(modeStr))
                {
                    modeStr = "EditMode";
                }

                if (!ModeParser.TryParse(modeStr, out var parsedMode, out var parseError))
                {
                    return Task.FromResult<object>(new ErrorResponse(parseError));
                }

                var p = new ToolParams(@params);
                bool includeDetails = p.GetBool("includeDetails");
                bool includeFailed = p.GetBool("includeFailed") || p.GetBool("includeFailedTests");
                bool includeSkipped = p.GetBool("includeSkipped");

                var filterOptions = GetFilterOptions(@params);
                long initTimeoutMs = p.GetInt("initTimeout") ?? 0;
                string requestToken = @params?["requestToken"]?.ToString()
                    ?? @params?["request_token"]?.ToString();
                string jobId = TestJobManager.StartJob(
                    parsedMode.Value, filterOptions, initTimeoutMs, requestToken);

                return Task.FromResult<object>(new SuccessResponse("Test job started.", new
                {
                    job_id = jobId,
                    request_token = string.IsNullOrWhiteSpace(requestToken) ? null : requestToken.Trim(),
                    status = "running",
                    mode = parsedMode.Value.ToString(),
                    include_details = includeDetails,
                    include_failed = includeFailed,
                    include_skipped = includeSkipped
                }));
            }
            catch (Exception ex)
            {
                // Normalize the already-running case to a stable error token. The active job id
                // and its originating request token let a caller whose run_tests reply was lost
                // distinguish its own run from another client's job.
                if (ex.Message != null && ex.Message.IndexOf("already in progress", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return Task.FromResult<object>(new ErrorResponse("tests_running", new
                    {
                        reason = "tests_running",
                        retry_after_ms = 5000,
                        job_id = TestJobManager.CurrentJobId,
                        request_token = TestJobManager.CurrentRequestToken
                    }));
                }
                return Task.FromResult<object>(new ErrorResponse($"Failed to start test job: {ex.Message}"));
            }
        }

        /// <summary>
        /// Unity Test Framework cannot build a run against assemblies that failed to compile.
        /// Returns the refusal — carrying the errors themselves — instead of starting a run that
        /// dies during initialization and leaves the caller guessing why, or null when the
        /// project is green.
        /// </summary>
        internal static object CompileErrorRefusal(bool compilationFailed, int errorCount, JArray errors)
        {
            if (!compilationFailed && errorCount <= 0)
            {
                return null;
            }

            return new ErrorResponse("compile_errors", new
            {
                reason = "compile_errors",
                errors = errorCount,
                error_details = errors,
                message = "Scripts do not compile; tests cannot run. "
                          + DescribeCompileErrors(errors, errorCount)
                          + "Fix the errors, then refresh_unity(compile=\"request\")."
            });
        }

        /// <summary>Renders the first few captured compile errors for the refusal message.</summary>
        private static string DescribeCompileErrors(JArray errors, int errorCount)
        {
            if (errors == null || errors.Count == 0)
            {
                return string.Empty;
            }

            var parts = errors.Take(3).Select(e =>
                $"{e["file"]}({e["line"]},{e["column"]}): {e["message"]}");
            string digest = string.Join("; ", parts);
            int remaining = Math.Max(0, errorCount - Math.Min(errors.Count, 3));
            return remaining > 0 ? $"{digest} (+{remaining} more). " : $"{digest}. ";
        }

        private static TestFilterOptions GetFilterOptions(JObject @params)
        {
            if (@params == null)
            {
                return null;
            }

            var p = new ToolParams(@params);
            var testNames = p.GetStringArray("testNames");
            var groupNames = p.GetStringArray("groupNames");
            var categoryNames = p.GetStringArray("categoryNames");
            var assemblyNames = p.GetStringArray("assemblyNames");

            if (testNames == null && groupNames == null && categoryNames == null && assemblyNames == null)
            {
                return null;
            }

            return new TestFilterOptions
            {
                TestNames = testNames,
                GroupNames = groupNames,
                CategoryNames = categoryNames,
                AssemblyNames = assemblyNames
            };
        }
    }
}
