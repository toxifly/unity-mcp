"""Compact discovery manifest for versioned Unity MCP capabilities."""

from typing import Any

from fastmcp import Context

from services.registry import get_registered_tools, mcp_for_unity_resource


_CAPABILITIES: dict[str, dict[str, Any]] = {
    "measure_ui": {
        "version": 1,
        "tools": ("measure_ui",),
    },
    "inspect_serialized": {
        "version": 1,
        "tools": ("inspect_serialized",),
    },
    "inspect_provenance": {
        "version": 1,
        "tools": ("inspect_provenance",),
    },
}


@mcp_for_unity_resource(
    uri="mcpforunity://capabilities",
    name="capabilities",
    description=(
        "Compact versioned manifest of available Unity MCP capabilities. "
        "Read this resource before relying on optional or versioned workflows.\n\n"
        "URI: mcpforunity://capabilities"
    ),
)
async def get_capabilities(ctx: Context) -> dict[str, Any]:
    """Return only capabilities backed by the server's registered tools."""
    del ctx
    registered_tools = {tool["name"] for tool in get_registered_tools()}
    capabilities = {
        name: {"version": definition["version"]}
        for name, definition in _CAPABILITIES.items()
        if all(tool in registered_tools for tool in definition["tools"])
    }
    return {
        "schema_version": 1,
        "tools": capabilities,
    }
