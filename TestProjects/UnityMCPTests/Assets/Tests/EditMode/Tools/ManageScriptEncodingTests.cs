using System.IO;
using System.Text;
using MCPForUnity.Editor.Tools;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Tools
{
    /// <summary>
    /// Covers what an edit leaves behind on disk besides the edit itself. A caller sends LF in JSON
    /// whatever the file uses and File.ReadAllText eats a BOM, so the naive read/write pair turned a
    /// three-line edit into a whole-file diff on any CRLF or BOM'd script.
    /// </summary>
    /// <remarks>
    /// Reads a scratch file outside Assets/ rather than driving apply_text_edits end to end: that
    /// path schedules an AssetDatabase refresh, and a script import during an EditMode run takes the
    /// test runner down with it.
    /// </remarks>
    [TestFixture]
    public class ManageScriptEncodingTests
    {
        private static readonly byte[] Bom = { 0xEF, 0xBB, 0xBF };
        private string _path;

        [SetUp]
        public void SetUp() => _path = Path.Combine(Path.GetTempPath(), "mcp-encoding-probe.cs");

        [TearDown]
        public void TearDown()
        {
            if (File.Exists(_path)) File.Delete(_path);
        }

        [Test]
        public void ACrlfFileIsReadAsCrlfAndInsertedLfAdoptsIt()
        {
            Write("class C\r\n{\r\n}\r\n", bom: false);

            string text = ManageScript.ReadTextPreservingEncoding(_path, out var encoding);

            Assert.AreEqual("crlf", encoding.Name);
            Assert.IsFalse(encoding.HasBom);
            StringAssert.Contains("\r\n", text);
            // The replacement text carries LF, the way a JSON payload always does.
            Assert.AreEqual("    int a;\r\n    int b;\r\n", encoding.Adopt("    int a;\n    int b;\n"));
        }

        [Test]
        public void AnchorInsertWithoutTrailingNewlineAppendsCrlf()
        {
            Write("class C\r\n{\r\n}\r\n", bom: false);

            string source = ManageScript.ReadTextPreservingEncoding(_path, out var encoding);
            int anchor = source.IndexOf('}');
            string result = source.Insert(
                anchor,
                encoding.AdoptWithTrailingLineEnding("    int value;"));

            StringAssert.Contains("    int value;\r\n}", result);
            Assert.IsFalse(result.Replace("\r\n", string.Empty).Contains("\n"),
                "anchor_insert introduced a bare LF into a CRLF file");
        }

        [Test]
        public void AnLfFileKeepsLfAndCrlfInputIsFlattenedToIt()
        {
            Write("class C\n{\n}\n", bom: false);

            ManageScript.ReadTextPreservingEncoding(_path, out var encoding);

            Assert.AreEqual("lf", encoding.Name);
            Assert.AreEqual("    int a;\n", encoding.Adopt("    int a;\r\n"));
        }

        [Test]
        public void AMixedFileFollowsWhicheverEndingItMostlyUses()
        {
            // Mixed files are real — a previous tool wrote LF into a CRLF file — and the majority
            // ending is the one that keeps the diff small.
            Write("a\r\nb\r\nc\r\nd\n", bom: false);

            ManageScript.ReadTextPreservingEncoding(_path, out var encoding);

            Assert.AreEqual("crlf", encoding.Name);
        }

        [Test]
        public void ABomIsSeenAndWrittenBackOut()
        {
            Write("class C\r\n{\r\n}\r\n", bom: true);

            string text = ManageScript.ReadTextPreservingEncoding(_path, out var encoding);

            Assert.IsTrue(encoding.HasBom);
            // Ordinal on purpose: the default StartsWith is culture-sensitive and U+FEFF has
            // no collation weight, so the culture overload answers true for every string.
            Assert.IsFalse(
                text.StartsWith("\uFEFF", System.StringComparison.Ordinal),
                "the BOM leaked into the text itself");

            File.WriteAllText(_path, text, encoding.Writer);
            byte[] after = File.ReadAllBytes(_path);
            Assert.AreEqual(Bom[0], after[0]);
            Assert.AreEqual(Bom[1], after[1]);
            Assert.AreEqual(Bom[2], after[2]);
        }

        [Test]
        public void AFileWithoutABomDoesNotGainOne()
        {
            Write("class C\n", bom: false);

            string text = ManageScript.ReadTextPreservingEncoding(_path, out var encoding);

            File.WriteAllText(_path, text, encoding.Writer);
            Assert.AreNotEqual(Bom[0], File.ReadAllBytes(_path)[0]);
        }

        [Test]
        public void AUtf16ScriptIsRefusedRatherThanDecodedToNonsense()
        {
            File.WriteAllText(_path, "class C\r\n{\r\n}\r\n", new UnicodeEncoding(false, true));

            Assert.Throws<InvalidDataException>(
                () => ManageScript.ReadTextPreservingEncoding(_path, out _));
        }

        private void Write(string text, bool bom)
        {
            byte[] payload = new UTF8Encoding(false).GetBytes(text);
            using var stream = File.Create(_path);
            if (bom) stream.Write(Bom, 0, Bom.Length);
            stream.Write(payload, 0, payload.Length);
        }
    }
}
