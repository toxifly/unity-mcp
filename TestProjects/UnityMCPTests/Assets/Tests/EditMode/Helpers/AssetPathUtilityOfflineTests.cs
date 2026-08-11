using System;
using System.IO;
using NUnit.Framework;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Constants;
using UnityEditor;

namespace MCPForUnityTests.Editor.Helpers
{
    public class AssetPathUtilityOfflineTests
    {
        private bool _originalForceRefresh;

        [SetUp]
        public void SetUp()
        {
            _originalForceRefresh = EditorPrefs.GetBool(EditorPrefKeys.DevModeForceServerRefresh, false);
        }

        [TearDown]
        public void TearDown()
        {
            EditorPrefs.SetBool(EditorPrefKeys.DevModeForceServerRefresh, _originalForceRefresh);
        }

        [Test]
        public void ShouldUseUvxOffline_WhenForceRefreshEnabled_ReturnsFalse()
        {
            EditorPrefs.SetBool(EditorPrefKeys.DevModeForceServerRefresh, true);
            Assert.IsFalse(AssetPathUtility.ShouldUseUvxOffline());
        }

        [Test]
        public void ShouldUseUvxOffline_DoesNotThrow()
        {
            EditorPrefs.SetBool(EditorPrefKeys.DevModeForceServerRefresh, false);
            Assert.DoesNotThrow(() => AssetPathUtility.ShouldUseUvxOffline());
        }

        [TestCase("10.1.1-beta.1", "10.1.1b1")]
        [TestCase("10.1.1-alpha.2", "10.1.1a2")]
        [TestCase("10.1.1-rc.3", "10.1.1rc3")]
        [TestCase("10.1.1-preview", "10.1.1rc0")]
        [TestCase("10.1.1", "10.1.1")]
        public void NormalizePythonPackageVersion_ProducesExactPep440Pin(
            string packageVersion, string expected)
        {
            Assert.AreEqual(expected, AssetPathUtility.NormalizePythonPackageVersion(packageVersion));
        }

        [TestCase("10.1")]
        [TestCase("10.1.1.2")]
        [TestCase("01.1.1")]
        [TestCase("10.01.1")]
        [TestCase("10.1.01")]
        [TestCase("10.1.1-beta.01")]
        [TestCase("10.1.1+")]
        [TestCase("10.1.1+build.7")]
        [TestCase("10.1.1-beta.1+build.7")]
        [TestCase("v10.1.1")]
        [TestCase("v10.1.1-beta.1")]
        [TestCase(" 10.1.1")]
        [TestCase("10.1.1 ")]
        public void NormalizePythonPackageVersion_RejectsMalformedSemVer(string packageVersion)
        {
            Assert.Throws<InvalidOperationException>(
                () => AssetPathUtility.NormalizePythonPackageVersion(packageVersion));
        }

        [TestCase("10.1.1", "mcpforunityserver==10.1.1")]
        [TestCase("10.1.1-beta.1", "mcpforunityserver==10.1.1b1")]
        public void BuildPinnedServerPackageSource_AlwaysUsesExactRelease(
            string packageVersion, string expected)
        {
            Assert.AreEqual(expected, AssetPathUtility.BuildPinnedServerPackageSource(packageVersion));
        }

        [Test]
        public void GetMcpServerPackageSource_PrereleasePackageUsesExactPin()
        {
            string originalOverride = EditorPrefs.GetString(EditorPrefKeys.GitUrlOverride, string.Empty);
            try
            {
                EditorPrefs.DeleteKey(EditorPrefKeys.GitUrlOverride);
                string expectedVersion = AssetPathUtility.NormalizePythonPackageVersion(
                    AssetPathUtility.GetPackageVersion());
                Assert.AreEqual(
                    $"mcpforunityserver=={expectedVersion}",
                    AssetPathUtility.GetMcpServerPackageSource());
            }
            finally
            {
                EditorPrefs.SetString(EditorPrefKeys.GitUrlOverride, originalOverride);
            }
        }

        [Test]
        public void GetBetaServerFromArgs_ExactPrereleasePinDoesNotEnableRangeResolution()
        {
            const string source = "mcpforunityserver==10.1.1b1";

            Assert.AreEqual(
                $"--from \"{source}\"",
                AssetPathUtility.GetBetaServerFromArgs(string.Empty, source, quoteFromPath: true));
            CollectionAssert.AreEqual(
                new[] { "--from", source },
                AssetPathUtility.GetBetaServerFromArgsList(string.Empty, source));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("mcpforunityserver")]
        [TestCase("mcpforunityserver>=10.1.0")]
        [TestCase("mcpforunityserver==10.1.*")]
        public void GetBetaServerFromArgs_RejectsMissingOrBroadPackageSource(string packageSource)
        {
            Assert.Throws<InvalidOperationException>(
                () => AssetPathUtility.GetBetaServerFromArgs(
                    string.Empty, packageSource, quoteFromPath: true));
            Assert.Throws<InvalidOperationException>(
                () => AssetPathUtility.GetBetaServerFromArgsList(
                    string.Empty, packageSource));
        }

        [TestCase("mcpforunityserver")]
        [TestCase("mcpforunityserver>=10.1.0")]
        [TestCase("git+https://github.com/CoplayDev/unity-mcp.git")]
        public void GetBetaServerFromArgs_RejectsNonLocalOverrideEvenWithPinnedFallback(
            string sourceOverride)
        {
            const string pinnedFallback = "mcpforunityserver==10.1.1b1";
            Assert.Throws<InvalidOperationException>(
                () => AssetPathUtility.GetBetaServerFromArgs(
                    sourceOverride, pinnedFallback, quoteFromPath: true));
            Assert.Throws<InvalidOperationException>(
                () => AssetPathUtility.GetBetaServerFromArgsList(
                    sourceOverride, pinnedFallback));
        }

        [TestCase("mcpforunityserver")]
        [TestCase("mcpforunityserver>=10.1.0")]
        [TestCase("mcpforunityserver~=10.1")]
        [TestCase("mcpforunityserver==10.1.1b1")]
        [TestCase("git+https://github.com/CoplayDev/unity-mcp.git")]
        [TestCase("https://github.com/CoplayDev/unity-mcp.git")]
        public void GetMcpServerPackageSource_RejectsNonLocalOverrides(string sourceOverride)
        {
            string originalOverride = EditorPrefs.GetString(EditorPrefKeys.GitUrlOverride, string.Empty);
            try
            {
                EditorPrefs.SetString(EditorPrefKeys.GitUrlOverride, sourceOverride);
                var error = Assert.Throws<InvalidOperationException>(
                    () => AssetPathUtility.GetMcpServerPackageSource());
                StringAssert.Contains("explicit local Server/", error.Message);
            }
            finally
            {
                EditorPrefs.SetString(EditorPrefKeys.GitUrlOverride, originalOverride);
            }
        }

        [Test]
        public void GetMcpServerPackageSource_AcceptsExplicitLocalServerDirectory()
        {
            string originalOverride = EditorPrefs.GetString(EditorPrefKeys.GitUrlOverride, string.Empty);
            string tempRoot = Path.Combine(Path.GetTempPath(), $"unity-mcp-source-{Guid.NewGuid():N}");
            string serverDirectory = Path.Combine(tempRoot, "Server");
            try
            {
                Directory.CreateDirectory(serverDirectory);
                File.WriteAllText(Path.Combine(serverDirectory, "pyproject.toml"), "[project]\nname='mcpforunityserver'\n");
                EditorPrefs.SetString(EditorPrefKeys.GitUrlOverride, serverDirectory);

                Assert.AreEqual(serverDirectory, AssetPathUtility.GetMcpServerPackageSource());
            }
            finally
            {
                EditorPrefs.SetString(EditorPrefKeys.GitUrlOverride, originalOverride);
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
        }

        [Test]
        public void GetMcpServerPackageSource_AcceptsEscapedFileUriAndPreservesSource()
        {
            string originalOverride = EditorPrefs.GetString(EditorPrefKeys.GitUrlOverride, string.Empty);
            string tempRoot = Path.Combine(Path.GetTempPath(), $"unity mcp source {Guid.NewGuid():N}");
            string serverDirectory = Path.Combine(tempRoot, "Server");
            try
            {
                Directory.CreateDirectory(serverDirectory);
                File.WriteAllText(Path.Combine(serverDirectory, "pyproject.toml"), "[project]\nname='mcpforunityserver'\n");
                string serverUri = new Uri(serverDirectory).AbsoluteUri;
                StringAssert.Contains("%20", serverUri);
                EditorPrefs.SetString(EditorPrefKeys.GitUrlOverride, serverUri);

                Assert.AreEqual(serverUri, AssetPathUtility.GetMcpServerPackageSource());
                Assert.AreEqual(serverUri, EditorPrefs.GetString(EditorPrefKeys.GitUrlOverride));
                CollectionAssert.AreEqual(
                    new[] { "--from", serverUri },
                    AssetPathUtility.GetBetaServerFromArgsList(
                        serverUri, "mcpforunityserver==10.1.1b1"));
            }
            finally
            {
                EditorPrefs.SetString(EditorPrefKeys.GitUrlOverride, originalOverride);
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
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
