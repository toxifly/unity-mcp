using MCPForUnity.Editor.Clients;
using MCPForUnity.Editor.Clients.Configurators;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Services;
using NUnit.Framework;
using UnityEditor;

namespace MCPForUnityTests.Editor.Clients
{
    /// <summary>
    /// The client section builds its manual snippet on construction, so a snippet that throws
    /// while Server Source is unset breaks the whole section. It reports the error instead.
    /// </summary>
    [TestFixture]
    public class ManualSnippetServerSourceTests
    {
        private bool _hadOverride;
        private string _originalOverride;
        private bool _originalUseHttp;

        [SetUp]
        public void SetUp()
        {
            _hadOverride = EditorPrefs.HasKey(EditorPrefKeys.GitUrlOverride);
            _originalOverride = EditorPrefs.GetString(EditorPrefKeys.GitUrlOverride, string.Empty);
            _originalUseHttp = EditorConfigurationCache.Instance.UseHttpTransport;
            EditorPrefs.DeleteKey(EditorPrefKeys.GitUrlOverride);
            EditorConfigurationCache.Instance.SetUseHttpTransport(false);
        }

        [TearDown]
        public void TearDown()
        {
            EditorConfigurationCache.Instance.SetUseHttpTransport(_originalUseHttp);
            if (_hadOverride)
                EditorPrefs.SetString(EditorPrefKeys.GitUrlOverride, _originalOverride);
            else
                EditorPrefs.DeleteKey(EditorPrefKeys.GitUrlOverride);
        }

        [Test]
        public void OpenCodeSnippet_WithoutServerSource_ReportsIt()
        {
            AssertReportsMissingServerSource(new OpenCodeConfigurator());
        }

        [Test]
        public void OpenClawSnippet_WithoutServerSource_ReportsIt()
        {
            AssertReportsMissingServerSource(new OpenClawConfigurator());
        }

        private static void AssertReportsMissingServerSource(McpClientConfiguratorBase configurator)
        {
            string snippet = null;
            Assert.DoesNotThrow(() => snippet = configurator.GetManualSnippet());
            StringAssert.Contains("Server Source is not set", snippet);
        }
    }
}
