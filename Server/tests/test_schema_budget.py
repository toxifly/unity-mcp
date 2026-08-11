"""Regression contract for the default MCP discovery payload."""

import os
import subprocess
import sys
import textwrap

MAX_TOOL_DESCRIPTION_CHARS = 1_000
MAX_TOOLS_LIST_ESTIMATED_TOKENS = 28_000
MIN_DUPLICATE_PARAGRAPH_CHARS = 80
EXPECTED_BASELINE_TOOLS_LIST_BYTES = 109_631
EXPECTED_MAX_REGRESSION_BYTES = EXPECTED_BASELINE_TOOLS_LIST_BYTES * 103 // 100


def test_default_discovery_stays_lean_and_tool_groups_round_trip():
    # integration/conftest.py installs a process-global fastmcp stub during
    # collection. Exercise the real installed client in an isolated process so
    # this contract is independent of pytest's collection order.
    script = textwrap.dedent(
        f"""
        import asyncio
        import json
        import re

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

                registered_descriptions = {{
                    tool["name"]: tool.get("description") or ""
                    for tool in get_registered_tools()
                }}
                oversized = {{
                    name: len(description)
                    for name, description in registered_descriptions.items()
                    if len(description) > {MAX_TOOL_DESCRIPTION_CHARS}
                }}
                assert not oversized, oversized

                paragraph_owners = {{}}
                for name, description in registered_descriptions.items():
                    for paragraph in re.split(r"\\n\\s*\\n+", description):
                        normalized = re.sub(
                            r"\\s+", " ", paragraph
                        ).strip().casefold()
                        if len(normalized) < {MIN_DUPLICATE_PARAGRAPH_CHARS}:
                            continue
                        paragraph_owners.setdefault(normalized, []).append(name)
                duplicates = {{
                    paragraph[:160]: owners
                    for paragraph, owners in paragraph_owners.items()
                    if len(owners) > 1
                }}
                assert not duplicates, duplicates

                tools_list_json = json.dumps(
                    {{
                        "tools": [
                            tool.model_dump(mode="json", by_alias=True)
                            for tool in tools
                        ]
                    }},
                    separators=(",", ":"),
                    ensure_ascii=False,
                )
                tools_list_bytes = len(tools_list_json.encode("utf-8"))
                # MCP clients use different tokenizers. Four UTF-8 bytes per
                # token is the repository's stable, model-neutral CI proxy.
                estimated_tokens = (tools_list_bytes + 3) // 4
                assert estimated_tokens <= {MAX_TOOLS_LIST_ESTIMATED_TOKENS}, (
                    estimated_tokens,
                    tools_list_bytes,
                )
                assert tools_list_bytes <= {EXPECTED_MAX_REGRESSION_BYTES}, (
                    tools_list_bytes,
                    estimated_tokens,
                )
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
