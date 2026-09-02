using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Tools
{
    /// <summary>
    /// Covers the scripting-define parameter handling. The write itself is not exercised:
    /// changing defines recompiles and reloads the domain, which would kill the test run.
    /// </summary>
    public class ManageEditorScriptingDefinesTests
    {
        private static string[] Normalize(JToken token)
        {
            Assert.IsNull(ManageEditor.NormalizeDefines(token, out var defines));
            return defines;
        }

        [Test]
        public void ArrayIsTrimmedAndDeduplicatedInOrder()
        {
            var defines = Normalize(new JArray(" B ", "A", "B", ""));
            Assert.AreEqual(new[] { "B", "A" }, defines);
        }

        [Test]
        public void SemicolonStringMatchesUnitysOwnStorageFormat()
        {
            Assert.AreEqual(new[] { "A", "B" }, Normalize(JToken.FromObject("A;B")));
            Assert.AreEqual(new[] { "A", "B" }, Normalize(JToken.FromObject("A,B")));
        }

        [Test]
        public void StringifiedJsonArrayIsUnwrapped()
        {
            Assert.AreEqual(new[] { "A", "B" }, Normalize(JToken.FromObject("[\"A\", \"B\"]")));
        }

        [Test]
        public void EmptyListClearsRatherThanBeingTreatedAsMissing()
        {
            Assert.IsEmpty(Normalize(new JArray()));
            Assert.IsEmpty(Normalize(JValue.CreateNull()));
        }

        [Test]
        public void SymbolWithWhitespaceIsRejected()
        {
            string error = ManageEditor.NormalizeDefines(new JArray("MY FLAG"), out _);
            StringAssert.Contains("valid C#", error);
        }

        [TestCase("FEATURE-X")]
        [TestCase("1DEBUG")]
        [TestCase("FEATURE;OTHER")]
        [TestCase("true")]
        [TestCase("false")]
        public void NonConditionalCompilationIdentifierIsRejected(string symbol)
        {
            string error = ManageEditor.NormalizeDefines(new JArray(symbol), out _);
            StringAssert.Contains("valid C#", error);
        }

        [Test]
        public void UnicodeConditionalCompilationIdentifierIsAccepted()
        {
            Assert.AreEqual(new[] { "功能_2" }, Normalize(new JArray("功能_2")));
        }

        [Test]
        public void MalformedJsonArrayIsRejectedRatherThanSplitIntoGarbage()
        {
            string error = ManageEditor.NormalizeDefines(JToken.FromObject("[\"A\", ]]"), out _);
            StringAssert.Contains("JSON array", error);
        }

        [Test]
        public void SplitDefinesRoundTripsUnitysStoredValue()
        {
            Assert.AreEqual(new[] { "A", "B" }, ManageEditor.SplitDefines("A;B"));
            Assert.IsEmpty(ManageEditor.SplitDefines(""));
            Assert.IsEmpty(ManageEditor.SplitDefines(null));
        }

        [Test]
        public void MissingDefinesIsRejectedSoAnAccidentalClearCannotHappen()
        {
            var response = ManageEditor.HandleCommand(new JObject { ["action"] = "set_scripting_defines" });
            var payload = JObject.FromObject(response);

            Assert.IsFalse((bool)payload["success"]);
            StringAssert.Contains("'defines' parameter required", (string)payload["error"]);
        }

        [Test]
        public void GetScriptingDefinesReadsTheActiveTarget()
        {
            var response = ManageEditor.HandleCommand(new JObject { ["action"] = "get_scripting_defines" });
            var payload = JObject.FromObject(response);

            Assert.IsTrue((bool)payload["success"], (string)payload["error"]);
            Assert.IsNotNull(payload["data"]["target"]);
            Assert.IsInstanceOf<JArray>(payload["data"]["defines"]);
        }

        [Test]
        public void UnknownBuildTargetIsReported()
        {
            var response = ManageEditor.HandleCommand(new JObject
            {
                ["action"] = "get_scripting_defines",
                ["target"] = "nintendo64"
            });
            var payload = JObject.FromObject(response);

            Assert.IsFalse((bool)payload["success"]);
            StringAssert.Contains("nintendo64", (string)payload["error"]);
        }
    }
}
