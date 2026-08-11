using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Services.Transport
{
    /// <summary>
    /// Shared, mandatory compatibility contract for both bridge transports.
    /// </summary>
    internal static class BridgeProtocol
    {
        internal const int Version = 2;

        private static readonly string[] RequiredServerResources =
        {
            "mcpforunity://capabilities",
            "mcpforunity://workflow"
        };

        internal static JObject CreateUnityHandshake(
            string packageVersion,
            IEnumerable<string> registeredResources,
            IEnumerable<string> toolGroups)
        {
            return new JObject
            {
                ["bridge_protocol_version"] = Version,
                ["unity_package"] = new JObject
                {
                    ["version"] = packageVersion ?? "unknown",
                    ["registered_resources"] = new JArray(NormalizeList(registeredResources)),
                    ["tool_groups"] = new JArray(NormalizeList(toolGroups))
                }
            };
        }

        internal static JObject CompleteHandshake(JObject serverHandshake, JObject unityHandshake)
        {
            return new JObject
            {
                ["bridge_protocol_version"] = Version,
                ["python_server"] = serverHandshake?["python_server"]?.DeepClone(),
                ["unity_package"] = unityHandshake?["unity_package"]?.DeepClone()
            };
        }

        internal static bool ValidateServerHandshake(
            JObject handshake,
            string unityPackageVersion,
            out string error)
        {
            error = null;
            if (handshake == null)
            {
                error = $"Python server did not send the mandatory bridge handshake. Bridge protocol {Version} is required; update the Python server.";
                return false;
            }

            int? protocol = handshake.Value<int?>("bridge_protocol_version");
            if (protocol != Version)
            {
                error = $"Bridge protocol mismatch: Unity package requires {Version}, Python server sent {(protocol.HasValue ? protocol.Value.ToString() : "missing")}. Update the Python server and Unity package together.";
                return false;
            }

            if (handshake["python_server"] is not JObject server)
            {
                error = "Python server handshake is incomplete: missing python_server manifest.";
                return false;
            }

            string serverVersion = server.Value<string>("version");
            if (!VersionsAreCompatible(serverVersion, unityPackageVersion))
            {
                error = $"Server/package version mismatch: Python server='{serverVersion ?? "missing"}', Unity package='{unityPackageVersion ?? "missing"}'. Install matching releases and restart both the MCP server and Unity bridge.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(server.Value<string>("git_sha")))
            {
                error = "Python server handshake is incomplete: missing Git SHA.";
                return false;
            }

            var resources = ReadStringSet(server["registered_resources"]);
            if (resources.Count == 0)
            {
                error = "Python server handshake is incomplete: no registered resources were reported.";
                return false;
            }

            string[] missing = RequiredServerResources.Where(resource => !resources.Contains(resource)).ToArray();
            if (missing.Length > 0)
            {
                error = $"Python server registration is incomplete; missing mandatory resources: {string.Join(", ", missing)}. The running MCP server is stale or from a different install.";
                return false;
            }

            if (ReadStringSet(server["tool_groups"]).Count == 0)
            {
                error = "Python server handshake is incomplete: no registered tool groups were reported.";
                return false;
            }

            return true;
        }

        internal static bool ValidateCompleteHandshake(
            JObject handshake,
            string unityPackageVersion,
            IEnumerable<string> registeredResources,
            IEnumerable<string> toolGroups,
            out string error)
        {
            if (!ValidateServerHandshake(handshake, unityPackageVersion, out error))
            {
                return false;
            }

            if (handshake["unity_package"] is not JObject unityPackage)
            {
                error = "Bridge handshake acknowledgement is incomplete: missing unity_package manifest.";
                return false;
            }

            if (!VersionsAreCompatible(unityPackage.Value<string>("version"), unityPackageVersion))
            {
                error = "Bridge handshake acknowledgement returned a different Unity package version.";
                return false;
            }

            if (!ReadStringSet(unityPackage["registered_resources"]).SetEquals(NormalizeList(registeredResources)))
            {
                error = "Bridge handshake acknowledgement returned a different Unity resource registration set.";
                return false;
            }

            if (!ReadStringSet(unityPackage["tool_groups"]).SetEquals(NormalizeList(toolGroups)))
            {
                error = "Bridge handshake acknowledgement returned a different Unity tool-group set.";
                return false;
            }

            return true;
        }

        internal static bool VersionsAreCompatible(string serverVersion, string unityPackageVersion)
        {
            string server = NormalizeVersion(serverVersion);
            string unity = NormalizeVersion(unityPackageVersion);
            return !string.IsNullOrEmpty(server)
                && !string.Equals(server, "unknown", StringComparison.Ordinal)
                && string.Equals(server, unity, StringComparison.Ordinal);
        }

        private static string NormalizeVersion(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string normalized = value.ToLowerInvariant();
            Match match = Regex.Match(
                normalized,
                @"^(?<base>(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*))(?:(?:-(?<semverLabel>alpha|beta|preview|pre|rc|a|b)(?:\.(?<semverNumber>0|[1-9][0-9]*))?)|(?<pepLabel>a|b|rc)(?<pepNumber>0|[1-9][0-9]*))?$",
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(2));
            if (!match.Success)
            {
                return string.Empty;
            }

            string label = match.Groups["pepLabel"].Value;
            string number = match.Groups["pepNumber"].Value;
            if (string.IsNullOrEmpty(label))
            {
                label = match.Groups["semverLabel"].Value;
                number = match.Groups["semverNumber"].Success
                    ? match.Groups["semverNumber"].Value
                    : "0";
                switch (label)
                {
                    case "alpha": label = "a"; break;
                    case "beta": label = "b"; break;
                    case "preview":
                    case "pre": label = "rc"; break;
                }
            }

            return string.IsNullOrEmpty(label)
                ? match.Groups["base"].Value
                : $"{match.Groups["base"].Value}{label}{number}";
        }

        private static SortedSet<string> NormalizeList(IEnumerable<string> values)
        {
            return new SortedSet<string>(
                (values ?? Array.Empty<string>()).Where(value => !string.IsNullOrWhiteSpace(value)),
                StringComparer.Ordinal);
        }

        private static SortedSet<string> ReadStringSet(JToken token)
        {
            if (token is not JArray array) return new SortedSet<string>(StringComparer.Ordinal);
            return NormalizeList(array.Values<string>());
        }
    }
}
