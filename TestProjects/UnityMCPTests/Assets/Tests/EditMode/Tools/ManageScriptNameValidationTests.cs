using System.Reflection;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Tools
{
    /// <summary>
    /// Covers the split between a script's FILE STEM and the TYPE it declares.
    ///
    /// manage_script's "name" parameter is a file stem — it is only recombined as "{name}.cs" — so it
    /// must accept the conventional partial-class layout "Type.Part.cs". The C# identifier rule applies
    /// to the derived TYPE name instead, and only where a type name is actually consumed.
    /// </summary>
    public class ManageScriptNameValidationTests
    {
        private static bool IsSafeFileStem(string stem, out string error)
        {
            var method = typeof(ManageScript).GetMethod(
                "IsSafeFileStem", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "IsSafeFileStem must exist");

            var args = new object[] { stem, null };
            bool ok = (bool)method.Invoke(null, args);
            error = (string)args[1];
            return ok;
        }

        private static string TypeNameFromFileStem(string stem)
        {
            var method = typeof(ManageScript).GetMethod(
                "TypeNameFromFileStem", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "TypeNameFromFileStem must exist");
            return (string)method.Invoke(null, new object[] { stem });
        }

        private static bool TryGetTypeName(string stem, out string typeName)
        {
            var method = typeof(ManageScript).GetMethod(
                "TryGetTypeName", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "TryGetTypeName must exist");

            var args = new object[] { stem, null, null };
            bool ok = (bool)method.Invoke(null, args);
            typeName = (string)args[1];
            return ok;
        }

        // --- File stem: the regression this suite exists for ---

        [Test]
        public void IsSafeFileStem_PartialClassDottedName_Accepted()
        {
            Assert.IsTrue(IsSafeFileStem("UnitSlotController.QuickMove", out string error),
                $"A partial-class file stem must be accepted, got: {error}");
        }

        [Test]
        public void IsSafeFileStem_PlainName_Accepted()
        {
            Assert.IsTrue(IsSafeFileStem("UnitSlotController", out _));
        }

        [Test]
        public void IsSafeFileStem_LeadingDigit_Accepted()
        {
            // Not a legal identifier, but a perfectly legal FILE name. The identifier rule is only
            // enforced where a type name is derived from it.
            Assert.IsTrue(IsSafeFileStem("123Foo", out _));
        }

        // --- File stem: path-safety rejections (the checks that replaced the identifier regex) ---

        [Test]
        public void IsSafeFileStem_PathSeparators_Rejected()
        {
            Assert.IsFalse(IsSafeFileStem("Scripts/Foo", out _), "forward slash must be rejected");
            Assert.IsFalse(IsSafeFileStem("Scripts\\Foo", out _), "backslash must be rejected on every platform");
        }

        [TestCase("Foo?Part")]
        [TestCase("Foo*Part")]
        [TestCase("Foo<Part")]
        [TestCase("Foo>Part")]
        [TestCase("Foo:Part")]
        [TestCase("Foo\"Part")]
        [TestCase("Foo|Part")]
        [TestCase("Foo\u001fPart")]
        public void IsSafeFileStem_PortableWindowsInvalidCharacters_Rejected(string stem)
        {
            Assert.IsFalse(IsSafeFileStem(stem, out _));
        }

        [Test]
        public void IsSafeFileStem_Traversal_Rejected()
        {
            Assert.IsFalse(IsSafeFileStem("..", out _));
            Assert.IsFalse(IsSafeFileStem("Foo..Bar", out _));
        }

        [Test]
        public void IsSafeFileStem_EmptyOrWhitespace_Rejected()
        {
            Assert.IsFalse(IsSafeFileStem("", out _));
            Assert.IsFalse(IsSafeFileStem("   ", out _));
        }

        [Test]
        public void IsSafeFileStem_LeadingDotOrTrailingDotOrSpace_Rejected()
        {
            Assert.IsFalse(IsSafeFileStem(".Hidden", out _), "a leading dot has no type name");
            Assert.IsFalse(IsSafeFileStem("Foo.", out _), "Windows strips a trailing dot");
            Assert.IsFalse(IsSafeFileStem("Foo ", out _), "Windows strips a trailing space");
        }

        [Test]
        public void IsSafeFileStem_ReservedDeviceName_Rejected()
        {
            Assert.IsFalse(IsSafeFileStem("CON", out _));
            Assert.IsFalse(IsSafeFileStem("nul", out _), "device names are case-insensitive");
            Assert.IsFalse(IsSafeFileStem("COM1.Part", out _),
                "a device name is still a device with a suffix after the first dot");
        }

        // --- Type name derivation ---

        [Test]
        public void TypeNameFromFileStem_TakesFirstSegment()
        {
            Assert.AreEqual("UnitSlotController", TypeNameFromFileStem("UnitSlotController.QuickMove"));
            Assert.AreEqual("UnitSlotController", TypeNameFromFileStem("UnitSlotController.Quick.Move"));
            Assert.AreEqual("Foo", TypeNameFromFileStem("Foo"));
        }

        [Test]
        public void TryGetTypeName_DottedStem_YieldsDeclaredType()
        {
            Assert.IsTrue(TryGetTypeName("UnitSlotController.QuickMove", out string typeName));
            Assert.AreEqual("UnitSlotController", typeName);
        }

        [Test]
        public void TryGetTypeName_NonIdentifierFirstSegment_Rejected()
        {
            Assert.IsFalse(TryGetTypeName("123Foo", out _), "a type name may not start with a digit");
            Assert.IsFalse(TryGetTypeName("123Foo.Part", out _));
        }

        // --- End to end through the command handler ---

        [Test]
        public void HandleCommand_ValidateDottedName_PassesTheNameGate()
        {
            var result = ManageScript.HandleCommand(new JObject
            {
                ["action"] = "validate",
                ["name"] = "SomeType.SomePartialPart",
                ["path"] = "Assets/Scripts",
            });

            // The file does not exist, so this still fails — but it must fail on the READ, proving the
            // dotted name got past validation instead of being rejected as an invalid script name.
            var error = result as ErrorResponse;
            Assert.IsNotNull(error, "a missing file should still produce an error response");
            StringAssert.DoesNotContain("Invalid script name", error.Error ?? string.Empty);
        }

        [Test]
        public void HandleCommand_ValidateNameWithSeparator_RejectedByTheNameGate()
        {
            var result = ManageScript.HandleCommand(new JObject
            {
                ["action"] = "validate",
                ["name"] = "Scripts/SomeType",
                ["path"] = "Assets/Scripts",
            });

            var error = result as ErrorResponse;
            Assert.IsNotNull(error);
            StringAssert.Contains("Invalid script name", error.Error ?? string.Empty);
        }
    }
}
