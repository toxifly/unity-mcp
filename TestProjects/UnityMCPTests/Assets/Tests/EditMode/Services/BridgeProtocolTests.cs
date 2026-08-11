using MCPForUnity.Editor.Services.Transport;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Services
{
    [TestFixture]
    public class BridgeProtocolTests
    {
        [Test]
        public void VersionsAreCompatible_SemVerBetaAndPep440Beta_AreEquivalent()
        {
            Assert.IsTrue(BridgeProtocol.VersionsAreCompatible(
                "10.1.1b1", "10.1.1-beta.1"));
            Assert.IsFalse(BridgeProtocol.VersionsAreCompatible(
                "10.1.0", "10.1.1-beta.1"));
        }

        [TestCase("10.1.1+build.7", "10.1.1")]
        [TestCase("10.1.1+build.7", "10.1.1+build.7")]
        [TestCase("10.1.1+build.7", "10.1.1+build.8")]
        [TestCase("10.1.1b1+build.7", "10.1.1-beta.1+build.7")]
        [TestCase("v10.1.1", "10.1.1")]
        [TestCase("v10.1.1", "v10.1.1")]
        public void VersionsAreCompatible_MetadataOrLeadingV_IsRejected(
            string serverVersion, string unityVersion)
        {
            Assert.IsFalse(BridgeProtocol.VersionsAreCompatible(
                serverVersion, unityVersion));
        }

        [Test]
        public void ValidateServerHandshake_MissingCapabilities_FailsClearly()
        {
            var handshake = new JObject
            {
                ["bridge_protocol_version"] = BridgeProtocol.Version,
                ["python_server"] = new JObject
                {
                    ["version"] = "10.1.1b1",
                    ["git_sha"] = "abcdef123456",
                    ["registered_resources"] = new JArray("mcpforunity://workflow"),
                    ["tool_groups"] = new JArray("core")
                }
            };

            Assert.IsFalse(BridgeProtocol.ValidateServerHandshake(
                handshake, "10.1.1-beta.1", out string error));
            StringAssert.Contains("mcpforunity://capabilities", error);
            StringAssert.Contains("stale", error.ToLowerInvariant());
        }

        [Test]
        public void ValidateServerHandshake_CompleteManifest_Succeeds()
        {
            var handshake = new JObject
            {
                ["bridge_protocol_version"] = BridgeProtocol.Version,
                ["python_server"] = new JObject
                {
                    ["version"] = "10.1.1b1",
                    ["git_sha"] = "abcdef123456",
                    ["registered_resources"] = new JArray(
                        "mcpforunity://capabilities",
                        "mcpforunity://workflow"),
                    ["tool_groups"] = new JArray("core", "testing")
                }
            };

            Assert.IsTrue(BridgeProtocol.ValidateServerHandshake(
                handshake, "10.1.1-beta.1", out string error), error);
        }
    }
}
