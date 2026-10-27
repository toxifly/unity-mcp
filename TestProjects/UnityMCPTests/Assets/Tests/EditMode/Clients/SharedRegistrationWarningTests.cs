using System;
using System.IO;
using MCPForUnity.Editor.Clients;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Clients
{
    /// <summary>
    /// Configure and Unregister touch only the local-scope registration, so they name any
    /// user- or project-scope UnityMCP registration Claude Code would still see.
    /// </summary>
    [TestFixture]
    public class SharedRegistrationWarningTests
    {
        private string _home;
        private string _project;

        [SetUp]
        public void SetUp()
        {
            string root = Path.Combine(Path.GetTempPath(), "UnityMCPTests", Guid.NewGuid().ToString("N"));
            _home = Path.Combine(root, "home");
            _project = Path.Combine(root, "project");
            Directory.CreateDirectory(_home);
            Directory.CreateDirectory(_project);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(Path.GetDirectoryName(_home), true); } catch { }
        }

        [Test]
        public void OnlyLocalScopeRegistrations_NeedNoWarning()
        {
            File.WriteAllText(Path.Combine(_home, ".claude.json"), new JObject
            {
                ["mcpServers"] = new JObject { ["other"] = new JObject() },
                ["projects"] = new JObject
                {
                    [_project] = new JObject { ["mcpServers"] = new JObject { ["UnityMCP"] = new JObject() } }
                }
            }.ToString());
            WriteServers(Path.Combine(_project, ".mcp.json"), "other");

            Assert.IsNull(ClaudeCliMcpConfigurator.SharedRegistrationWarning(_project, _home, unregistered: true));
        }

        [Test]
        public void UserAndProjectRegistrations_AreNamedWithTheirRemoveCommands()
        {
            WriteServers(Path.Combine(_home, ".claude.json"), "unityMCP");
            WriteServers(Path.Combine(_project, ".mcp.json"), "UnityMCP");

            string warning = ClaudeCliMcpConfigurator.SharedRegistrationWarning(_project, _home, unregistered: true);

            StringAssert.Contains("claude mcp remove --scope user unityMCP", warning);
            StringAssert.Contains("claude mcp remove --scope project UnityMCP", warning);
            StringAssert.Contains("still launches", warning);
        }

        [Test]
        public void AnUnreadableProjectFile_DoesNotHideTheUserRegistration()
        {
            WriteServers(Path.Combine(_home, ".claude.json"), "UnityMCP");
            File.WriteAllText(Path.Combine(_project, ".mcp.json"), "{ not json");

            string warning = ClaudeCliMcpConfigurator.SharedRegistrationWarning(_project, _home, unregistered: false);

            StringAssert.Contains("claude mcp remove --scope user UnityMCP", warning);
            StringAssert.DoesNotContain("--scope project", warning);
            StringAssert.Contains("takes precedence", warning);
        }

        private static void WriteServers(string path, string name)
        {
            File.WriteAllText(path, new JObject
            {
                ["mcpServers"] = new JObject { [name] = new JObject { ["command"] = "uvx" } }
            }.ToString());
        }
    }
}
