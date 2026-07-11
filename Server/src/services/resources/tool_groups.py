"""
tool_groups resource – exposes available tool groups and their metadata.

URI: mcpforunity://tool-groups
"""
from typing import Any

from fastmcp import Context

from services.registry import (
    mcp_for_unity_resource,
    DEFAULT_ENABLED_GROUPS,
    get_group_visibility_states,
    parse_session_group_overrides,
)


@mcp_for_unity_resource(
    uri="mcpforunity://tool-groups",
    name="tool_groups",
    description=(
        "Available tool groups, their tools, and effective visibility. "
        "Use manage_tools to activate/deactivate groups per session.\n\n"
        "URI: mcpforunity://tool-groups"
    ),
)
async def get_tool_groups(ctx: Context) -> dict[str, Any]:
    session_overrides: dict[str, bool] = {}
    try:
        rules = await ctx._get_visibility_rules()
        session_overrides = parse_session_group_overrides(rules)
    except Exception:
        pass  # No active session or unsupported – fall back to defaults

    groups = get_group_visibility_states(session_overrides)
    return {
        "groups": groups,
        "total_groups": len(groups),
        "default_enabled": sorted(DEFAULT_ENABLED_GROUPS),
        "note": (
            "'enabled' is the effective state for this session; 'source' is "
            "session | unity | default. 'unity_persisted' is the Unity "
            "Editor's persisted toggle state from the last sync, if any."
        ),
        "usage": "Call manage_tools(action='activate', group='<name>') to enable a group.",
    }
