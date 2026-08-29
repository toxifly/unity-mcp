using System;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Poll a previously started async test job by job_id.
    /// </summary>
    [McpForUnityTool("get_test_job", AutoRegister = false, Group = "testing")]
    public static class GetTestJob
    {
        public static object HandleCommand(JObject @params)
        {
            string jobId = @params?["job_id"]?.ToString() ?? @params?["jobId"]?.ToString();
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return new ErrorResponse("Missing required parameter 'job_id'.");
            }

            var p = new ToolParams(@params);
            bool includeDetails = p.GetBool("includeDetails");
            // includeFailedTests is the old spelling, and it used to drag skipped tests along
            // with the failures. Skipped detail now needs asking for by name.
            bool includeFailed = p.GetBool("includeFailed") || p.GetBool("includeFailedTests");
            bool includeSkipped = p.GetBool("includeSkipped");

            var job = TestJobManager.GetJob(jobId);
            if (job == null)
            {
                return new ErrorResponse("Unknown job_id.");
            }

            var payload = TestJobManager.ToSerializable(
                job, includeDetails, includeFailed, includeSkipped);
            return new SuccessResponse("Test job status retrieved.", payload);
        }
    }
}
