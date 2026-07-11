"""Regression contract for the default MCP discovery payload."""

import os
import subprocess
import sys
import textwrap

EXPECTED_MAX_SCHEMA_BYTES = 120_000
EXPECTED_BASELINE_SCHEMA_BYTES = 111_411
EXPECTED_MAX_REGRESSION_BYTES = EXPECTED_BASELINE_SCHEMA_BYTES * 103 // 100


def test_default_discovery_stays_lean_and_tool_groups_round_trip():
    # integration/conftest.py installs a process-global fastmcp stub during
    # collection. Exercise the real installed client in an isolated process so
    # this contract is independent of pytest's collection order.
    script = textwrap.dedent(
        f"""
        import asyncio
        import json

        from fastmcp import Client
        from fastmcp.client.messages import MessageHandler
        from main import _build_instructions, create_mcp_server
        from services.registry import (
            DEFAULT_ENABLED_GROUPS,
            get_registered_tools,
        )

        class ToolListChangedRecorder(MessageHandler):
            def __init__(self):
                self.count = 0

            async def on_tool_list_changed(self, message):
                self.count += 1

        async def check():
            server = create_mcp_server(project_scoped_tools=True)
            notifications = ToolListChangedRecorder()
            async with Client(server, message_handler=notifications) as client:
                tools = await client.list_tools()
                resources = await client.list_resources()
                capabilities = await client.read_resource(
                    "mcpforunity://capabilities"
                )

                descriptions = "\\n".join(
                    tool.description or "" for tool in tools
                )
                assert "This server provides tools" not in descriptions
                assert "Targeting Unity instances" not in descriptions

                schema_bytes = sum(
                    len(json.dumps(tool.model_dump(), separators=(",", ":")))
                    for tool in tools
                )
                assert schema_bytes < {EXPECTED_MAX_SCHEMA_BYTES}, schema_bytes
                assert schema_bytes <= {EXPECTED_MAX_REGRESSION_BYTES}, schema_bytes
                assert len(_build_instructions(project_scoped_tools=True)) < 300

                resource_uris = {{str(resource.uri) for resource in resources}}
                assert "mcpforunity://workflow" in resource_uris
                capability_text = "\\n".join(
                    content.text
                    for content in capabilities
                    if getattr(content, "text", None)
                )
                assert "mcpforunity://workflow" in capability_text

                inactive_tools = {{
                    tool["name"]
                    for tool in get_registered_tools()
                    if tool["group"] is not None
                    and tool["group"] not in DEFAULT_ENABLED_GROUPS
                }}
                initial_tools = {{tool.name for tool in tools}}
                assert inactive_tools.isdisjoint(initial_tools), initial_tools

                docs_tools = {{"unity_docs", "unity_reflect"}}
                assert notifications.count == 0
                await client.call_tool(
                    "manage_tools", {{"action": "activate", "group": "docs"}}
                )
                active_tools = {{tool.name for tool in await client.list_tools()}}
                assert docs_tools <= active_tools
                assert notifications.count == 1

                await client.call_tool(
                    "manage_tools", {{"action": "deactivate", "group": "docs"}}
                )
                deactivated_tools = {{
                    tool.name for tool in await client.list_tools()
                }}
                assert docs_tools.isdisjoint(deactivated_tools)
                assert notifications.count == 2

                await client.call_tool(
                    "manage_tools", {{"action": "activate", "group": "docs"}}
                )
                await client.call_tool("manage_tools", {{"action": "reset"}})
                reset_tools = {{tool.name for tool in await client.list_tools()}}
                assert reset_tools == initial_tools
                assert notifications.count == 4

        asyncio.run(check())
        """
    )
    env = os.environ.copy()
    env.update({
        "UNITY_MCP_SKIP_STARTUP_CONNECT": "1",
        "DISABLE_TELEMETRY": "true",
    })
    result = subprocess.run(
        [sys.executable, "-c", script],
        cwd=os.path.dirname(os.path.dirname(__file__)),
        env=env,
        capture_output=True,
        text=True,
        timeout=30,
    )
    assert result.returncode == 0, result.stdout + result.stderr
