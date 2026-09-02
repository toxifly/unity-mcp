using MCPForUnity.Editor.Services;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;

namespace MCPForUnityTests.Editor.Services
{
    /// <summary>
    /// Covers the compiler-error shape that refresh_unity returns inline, so a red compile
    /// names what broke without a follow-up read_console.
    /// </summary>
    public class CompilationStateTrackerTests
    {
        private static CompilerMessage Message(string file, int line, int column, string text)
        {
            return new CompilerMessage
            {
                file = file,
                line = line,
                column = column,
                message = text,
                type = CompilerMessageType.Error
            };
        }

        [Test]
        public void Describe_StripsLocationPrefixAlreadyCarriedByStructuredFields()
        {
            var described = CompilationStateTracker.Describe(Message(
                "Assets/Scripts/Player.cs", 42, 9,
                "Assets/Scripts/Player.cs(42,9): error CS1002: ; expected"));

            Assert.AreEqual("Assets/Scripts/Player.cs", (string)described["file"]);
            Assert.AreEqual(42, (int)described["line"]);
            Assert.AreEqual(9, (int)described["column"]);
            Assert.AreEqual("error CS1002: ; expected", (string)described["message"]);
        }

        [Test]
        public void Describe_KeepsMessageWhenItDoesNotStartWithTheFile()
        {
            const string text = "error CS0006: Metadata file 'Foo.dll' could not be found";
            var described = CompilationStateTracker.Describe(Message("Assets/Scripts/Player.cs", 0, 0, text));

            Assert.AreEqual(text, (string)described["message"]);
        }

        [Test]
        public void Describe_KeepsParenthesesThatBelongToTheDiagnostic()
        {
            const string text = "error CS1503: Argument 1: cannot convert from 'int' to 'string'";
            var described = CompilationStateTracker.Describe(Message("Assets/A.cs", 1, 1, text));

            Assert.AreEqual(text, (string)described["message"]);
        }

        [Test]
        public void Describe_TruncatesRunawayMessages()
        {
            var described = CompilationStateTracker.Describe(
                Message("Assets/A.cs", 1, 1, new string('x', 900)));

            string message = (string)described["message"];
            Assert.Less(message.Length, 900);
            Assert.IsTrue(message.EndsWith("..."), message);
        }

        [Test]
        public void LastErrorDetails_IsNullUnlessErrorsWereActuallyCaptured()
        {
            // A clean compile clears this key, and the server treats "no details" as
            // "nothing to inline" -- an empty or unparseable record must read the same way.
            const string key = "MCPForUnity.Compilation.ErrorDetails";
            string original = SessionState.GetString(key, string.Empty);
            try
            {
                SessionState.SetString(key, string.Empty);
                Assert.IsNull(CompilationStateTracker.LastErrorDetails);

                SessionState.SetString(key, "[]");
                Assert.IsNull(CompilationStateTracker.LastErrorDetails);

                SessionState.SetString(key, "not json");
                Assert.IsNull(CompilationStateTracker.LastErrorDetails);

                SessionState.SetString(key, "[{\"file\":\"Assets/A.cs\"}]");
                Assert.AreEqual(1, CompilationStateTracker.LastErrorDetails.Count);
            }
            finally
            {
                SessionState.SetString(key, original);
            }
        }
    }
}
