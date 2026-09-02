using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Tools
{
    /// <summary>
    /// run_tests must refuse a red project and say what broke. Exercised through the pure gate so
    /// the assertions never risk starting a real Unity Test Framework run.
    /// </summary>
    public class RunTestsCompileGateTests
    {
        private static JArray Errors(int count)
        {
            var errors = new JArray();
            for (int i = 1; i <= count; i++)
            {
                errors.Add(new JObject
                {
                    ["file"] = $"Assets/Scripts/File{i}.cs",
                    ["line"] = i,
                    ["column"] = 3,
                    ["message"] = "error CS1002: ; expected"
                });
            }
            return errors;
        }

        private static JObject Refusal(object response)
        {
            Assert.IsInstanceOf<ErrorResponse>(response);
            return JObject.FromObject(response);
        }

        [Test]
        public void GreenProjectIsNotRefused()
        {
            Assert.IsNull(RunTests.CompileErrorRefusal(false, 0, null));
        }

        [Test]
        public void RefusalCarriesTheErrorsThemselves()
        {
            var refusal = Refusal(RunTests.CompileErrorRefusal(false, 2, Errors(2)));

            Assert.AreEqual("compile_errors", (string)refusal["error"]);
            Assert.AreEqual(2, (int)refusal["data"]["errors"]);
            Assert.AreEqual(2, ((JArray)refusal["data"]["error_details"]).Count);

            string message = (string)refusal["data"]["message"];
            StringAssert.Contains("Assets/Scripts/File1.cs(1,3): error CS1002: ; expected", message);
            StringAssert.Contains("refresh_unity", message);
        }

        [Test]
        public void RefusalDigestCapsAtThreeAndCountsTheRest()
        {
            var refusal = Refusal(RunTests.CompileErrorRefusal(false, 5, Errors(5)));
            string message = (string)refusal["data"]["message"];

            StringAssert.Contains("(+2 more)", message);
            StringAssert.DoesNotContain("File4.cs", message);
        }

        [Test]
        public void RefusalCountsErrorsBeyondTheCapturedDetails()
        {
            // CompilationStateTracker keeps at most ten details, but the total remains exact.
            var refusal = Refusal(RunTests.CompileErrorRefusal(false, 20, Errors(10)));
            string message = (string)refusal["data"]["message"];

            StringAssert.Contains("(+17 more)", message);
            StringAssert.DoesNotContain("File4.cs", message);
        }

        [Test]
        public void FailedCompilationIsRefusedEvenWithoutCapturedCounters()
        {
            // Scripts that were already red when the editor started compile before our tracker
            // subscribes, so scriptCompilationFailed is the only signal available.
            var refusal = Refusal(RunTests.CompileErrorRefusal(true, 0, null));

            Assert.AreEqual("compile_errors", (string)refusal["error"]);
            StringAssert.Contains("Scripts do not compile", (string)refusal["data"]["message"]);
        }
    }
}
