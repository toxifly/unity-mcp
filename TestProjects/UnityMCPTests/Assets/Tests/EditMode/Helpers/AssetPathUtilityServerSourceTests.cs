using System;
using System.IO;
using NUnit.Framework;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Services;
using UnityEditor;

namespace MCPForUnityTests.Editor.Helpers
{
    public class AssetPathUtilityServerSourceTests
    {
        private static readonly string UvxDirectory = Path.Combine(Path.GetTempPath(), "uv-bin");

        private bool _hadOverride;
        private string _originalOverride;
        private IPathResolverService _originalPaths;

        [SetUp]
        public void SetUp()
        {
            _hadOverride = EditorPrefs.HasKey(EditorPrefKeys.GitUrlOverride);
            _originalOverride = EditorPrefs.GetString(EditorPrefKeys.GitUrlOverride, string.Empty);
            _originalPaths = MCPServiceLocator.Paths;
            MCPServiceLocator.Register<IPathResolverService>(new FixedUvxPathResolver(Path.Combine(UvxDirectory, "uvx")));
        }

        [TearDown]
        public void TearDown()
        {
            if (_hadOverride)
                EditorPrefs.SetString(EditorPrefKeys.GitUrlOverride, _originalOverride);
            else
                EditorPrefs.DeleteKey(EditorPrefKeys.GitUrlOverride);
            MCPServiceLocator.Register(_originalPaths);
        }

        [Test]
        public void GetServerProjectDirectory_WhenUnset_RequiresServerSource()
        {
            EditorPrefs.DeleteKey(EditorPrefKeys.GitUrlOverride);

            var error = Assert.Throws<InvalidOperationException>(
                () => AssetPathUtility.GetServerProjectDirectory());
            StringAssert.Contains("Server Source is not set", error.Message);
            Assert.IsNull(AssetPathUtility.TryGetServerProjectDirectory());
        }

        [TestCase("mcpforunityserver")]
        [TestCase("mcpforunityserver>=10.1.0")]
        [TestCase("mcpforunityserver==10.1.1b1")]
        [TestCase("git+https://github.com/CoplayDev/unity-mcp.git")]
        [TestCase("https://github.com/CoplayDev/unity-mcp.git")]
        [TestCase("/nonexistent/unity-mcp/Server")]
        public void GetServerProjectDirectory_RejectsAnythingButALocalServerCheckout(string sourceOverride)
        {
            EditorPrefs.SetString(EditorPrefKeys.GitUrlOverride, sourceOverride);

            var error = Assert.Throws<InvalidOperationException>(
                () => AssetPathUtility.GetServerProjectDirectory());
            StringAssert.Contains("explicit local Server/", error.Message);
            Assert.IsNull(AssetPathUtility.TryGetServerProjectDirectory());
        }

        [Test]
        public void GetServerProjectDirectory_ReturnsLocalServerDirectory()
        {
            using var source = new TempServerSource();

            Assert.AreEqual(source.ServerDirectory, AssetPathUtility.GetServerProjectDirectory());
        }

        [Test]
        public void GetServerProjectDirectory_CorrectsRepositoryRootToServerAndPersistsIt()
        {
            using var source = new TempServerSource();
            EditorPrefs.SetString(EditorPrefKeys.GitUrlOverride, source.Root);

            Assert.AreEqual(source.ServerDirectory, AssetPathUtility.GetServerProjectDirectory());
            Assert.AreEqual(source.ServerDirectory, EditorPrefs.GetString(EditorPrefKeys.GitUrlOverride));
        }

        [Test]
        public void TryGetServerProjectDirectory_ResolvesARepositoryRootWithoutRewritingTheSetting()
        {
            using var source = new TempServerSource();
            EditorPrefs.SetString(EditorPrefKeys.GitUrlOverride, source.Root);

            Assert.AreEqual(source.ServerDirectory, AssetPathUtility.TryGetServerProjectDirectory());
            Assert.AreEqual(source.Root, EditorPrefs.GetString(EditorPrefKeys.GitUrlOverride));
        }

        [Test]
        public void GetServerProjectDirectory_DropsATrailingSeparator()
        {
            using var source = new TempServerSource();
            EditorPrefs.SetString(EditorPrefKeys.GitUrlOverride, source.ServerDirectory + Path.DirectorySeparatorChar);

            Assert.AreEqual(source.ServerDirectory, AssetPathUtility.GetServerProjectDirectory());
        }

        [Test]
        public void GetServerProjectDirectory_ResolvesEscapedFileUriAndPreservesSource()
        {
            using var source = new TempServerSource($"unity mcp source {Guid.NewGuid():N}");
            string serverUri = new Uri(source.ServerDirectory).AbsoluteUri;
            StringAssert.Contains("%20", serverUri);
            EditorPrefs.SetString(EditorPrefKeys.GitUrlOverride, serverUri);

            Assert.AreEqual(source.ServerDirectory, AssetPathUtility.GetServerProjectDirectory());
            Assert.AreEqual(serverUri, EditorPrefs.GetString(EditorPrefKeys.GitUrlOverride));
        }

        [Test]
        public void GetServerCommand_RunsLockedServerCheckoutWithUv()
        {
            using var source = new TempServerSource();

            var (command, args) = AssetPathUtility.GetServerCommand();

            Assert.AreEqual(Path.Combine(UvxDirectory, "uv"), command);
            CollectionAssert.AreEqual(
                new[] { "run", "--frozen", "--project", source.ServerDirectory, "mcp-for-unity" }, args);
        }

        [Test]
        public void GetStdioServerCommand_AppendsStdioTransport()
        {
            using var source = new TempServerSource();

            var (command, args) = AssetPathUtility.GetStdioServerCommand();

            Assert.AreEqual(Path.Combine(UvxDirectory, "uv"), command);
            CollectionAssert.AreEqual(
                new[] { "run", "--frozen", "--project", source.ServerDirectory, "mcp-for-unity", "--transport", "stdio" },
                args);
        }

        [Test]
        public void GetStdioServerCommandLine_QuotesPartsWithSpaces()
        {
            using var source = new TempServerSource($"unity mcp source {Guid.NewGuid():N}");
            string uvxDirectory = Path.Combine(Path.GetTempPath(), "my uv");
            MCPServiceLocator.Register<IPathResolverService>(new FixedUvxPathResolver(Path.Combine(uvxDirectory, "uvx")));

            Assert.AreEqual(
                $"\"{Path.Combine(uvxDirectory, "uv")}\" run --frozen --project \"{source.ServerDirectory}\" mcp-for-unity --transport stdio",
                AssetPathUtility.GetStdioServerCommandLine());
        }

        [TestCase("plain", "plain")]
        [TestCase(@"C:\no-space\Server\", @"C:\no-space\Server\")]
        [TestCase(@"C:\My Projects\unity-mcp\Server\", @"""C:\My Projects\unity-mcp\Server\\""")]
        [TestCase(@"say ""hi""", @"""say \""hi\""""")]
        [TestCase(@"a\""b", @"""a\\\""b""")]
        [TestCase("", @"""""")]
        public void JoinCommandLine_QuotesEachPartSoArgvParsingReadsItBack(string part, string expected)
        {
            Assert.AreEqual($"run {expected} end", AssetPathUtility.JoinCommandLine(new[] { "run", part, "end" }));
        }

        [Test]
        public void GetServerCommand_WithoutUv_Throws()
        {
            using var source = new TempServerSource();
            MCPServiceLocator.Register<IPathResolverService>(new FixedUvxPathResolver(null));

            var error = Assert.Throws<InvalidOperationException>(() => AssetPathUtility.GetServerCommand());
            StringAssert.Contains("uv not found", error.Message);
        }

        [Test]
        public void ExtractServerProjectDirectory_ReadsProjectArgument()
        {
            Assert.AreEqual("/work/unity-mcp/Server", AssetPathUtility.ExtractServerProjectDirectory(
                new[] { "run", "--frozen", "--project", "/work/unity-mcp/Server", "mcp-for-unity" }));
        }

        [Test]
        public void ExtractServerProjectDirectory_ReturnsNullWithoutServerProject()
        {
            Assert.IsNull(AssetPathUtility.ExtractServerProjectDirectory(null));
            Assert.IsNull(AssetPathUtility.ExtractServerProjectDirectory(
                new[] { "--from", "mcpforunityserver==10.1.1", "mcp-for-unity" }));
            Assert.IsNull(AssetPathUtility.ExtractServerProjectDirectory(new[] { "run", "--project" }));
        }

        [Test]
        public void GetLocalServerCheckPath_DecodesUncFileUri()
        {
            const string source = "file://host/share/My%20Repo/Server";

            string checkPath = AssetPathUtility.GetLocalServerCheckPath(source);

            Assert.AreEqual(new Uri(source).LocalPath, checkPath);
            StringAssert.DoesNotContain("%20", checkPath);
        }
    }
}
