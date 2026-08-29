using System;
using System.Collections.Generic;

namespace MCPForUnity.Editor.Constants
{
    /// <summary>
    /// Tool-group visibility defaults shared with the Python server.
    /// Mirror of DEFAULT_ENABLED_GROUPS in Server/src/services/registry/tool_registry.py —
    /// keep both sides in sync when changing which groups are enabled by default.
    /// </summary>
    internal static class McpToolGroups
    {
        /// <summary>
        /// Version of the tool preference semantics, reported to the Python server via
        /// get_tool_states and register_tools. v1 (unversioned) treated every built-in
        /// tool as enabled by default; v2 derives defaults from tool groups. The server
        /// only trusts enabled states as intentional when this is >= 2.
        /// </summary>
        internal const int PreferencesVersion = 2;

        private static readonly HashSet<string> DefaultEnabledGroups =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "core", "testing" };

        internal static bool IsDefaultEnabled(string group)
        {
            return !string.IsNullOrEmpty(group) && DefaultEnabledGroups.Contains(group);
        }
    }
}
