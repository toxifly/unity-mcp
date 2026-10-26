using System;
using System.IO;
using MCPForUnity.Editor.Clients;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Models;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;

namespace MCPForUnityTests.Editor.Clients
{
    /// <summary>
    /// A stdio registration is current only when it runs the Server/ checkout set in
    /// Server Source; anything else (another checkout, a published package) is a mismatch.
    /// </summary>
    [TestFixture]
    public class CheckStatusServerSourceTests
    {
        private string _tempDir;
        private TempServerSource _serverSource;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "UnityMCPTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _serverSource = new TempServerSource();
        }

        [TearDown]
        public void TearDown()
        {
            _serverSource.Dispose();
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
        }

        [Test]
        public void CheckStatus_RegistrationRunningServerSource_IsConfigured()
        {
            McpClient client = CheckStdioRegistration(
                "run", "--frozen", "--project", _serverSource.ServerDirectory, "mcp-for-unity", "--transport", "stdio");

            Assert.AreEqual(McpStatus.Configured, client.status, client.configStatus);
            Assert.AreEqual(ConfiguredTransport.Stdio, client.configuredTransport);
        }

        [Test]
        public void CheckStatus_RegistrationRunningAnotherCheckout_IsMismatch()
        {
            string otherCheckout = Path.Combine(_tempDir, "other", "Server");

            McpClient client = CheckStdioRegistration(
                "run", "--frozen", "--project", otherCheckout, "mcp-for-unity", "--transport", "stdio");

            Assert.AreEqual(McpStatus.VersionMismatch, client.status);
            StringAssert.Contains("Server Source", client.configStatus);
        }

        [Test]
        public void CheckStatus_PublishedPackageRegistration_IsMismatch()
        {
            McpClient client = CheckStdioRegistration(
                "--from", "mcpforunityserver==10.1.1", "mcp-for-unity", "--transport", "stdio");

            Assert.AreEqual(McpStatus.VersionMismatch, client.status);
            StringAssert.Contains("Server Source", client.configStatus);
        }

        [Test]
        public void CheckStatus_WithoutServerSource_ReportsIt()
        {
            EditorPrefs.DeleteKey(EditorPrefKeys.GitUrlOverride);

            McpClient client = CheckStdioRegistration(
                "run", "--frozen", "--project", _serverSource.ServerDirectory, "mcp-for-unity", "--transport", "stdio");

            Assert.AreEqual(McpStatus.Error, client.status);
            StringAssert.Contains("Server Source is not set", client.configStatus);
        }

        private McpClient CheckStdioRegistration(params string[] args)
        {
            string configPath = Path.Combine(_tempDir, "mcp.json");
            var root = new JObject
            {
                ["mcpServers"] = new JObject
                {
                    ["unityMCP"] = new JObject
                    {
                        ["type"] = "stdio",
                        ["command"] = "uv",
                        ["args"] = new JArray(args)
                    }
                }
            };
            File.WriteAllText(configPath, root.ToString());

            var client = new McpClient
            {
                name = "Fake",
                windowsConfigPath = configPath,
                macConfigPath = configPath,
                linuxConfigPath = configPath,
            };
            new FakeJsonConfigurator(client).CheckStatus(attemptAutoRewrite: false);
            return client;
        }

        private sealed class FakeJsonConfigurator : JsonFileMcpConfigurator
        {
            public FakeJsonConfigurator(McpClient client) : base(client) { }
        }
    }
}
