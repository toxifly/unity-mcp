"""Regression contract for the default MCP discovery payload."""

import os
import subprocess
import sys
import textwrap

EXPECTED_MAX_SCHEMA_BYTES = 120_000


def test_default_discovery_stays_lean_and_links_workflow():
    # integration/conftest.py installs a process-global fastmcp stub during
    # collection. Exercise the real installed client in an isolated process so
    # this contract is independent of pytest's collection order.
    script = textwrap.dedent(
        f"""
        import asyncio
        import json

        from fastmcp import Client
        from main import _build_instructions, create_mcp_server
        from services.registry import (
            DEFAULT_ENABLED_GROUPS,
            get_registered_tools,
        )

        async def check():
            server = create_mcp_server(project_scoped_tools=True)
            async with Client(server) as client:
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
            visible_tools = {{tool.name for tool in tools}}
            assert inactive_tools.isdisjoint(visible_tools), visible_tools

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
