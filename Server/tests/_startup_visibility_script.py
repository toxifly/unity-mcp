"""Executed in an isolated subprocess by test_startup_visibility.py.

Walks the complete real startup sequence against an in-memory FastMCP server:
register all Python tools, apply default visibility, feed simulated Unity
``get_tool_states`` responses through the startup sync, then list MCP tools
and assert the effective visibility. Run in a subprocess because the
integration conftest installs a process-global fastmcp stub.
"""
import asyncio
import json
import sys
from pathlib import Path
from unittest.mock import AsyncMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from fastmcp import Client  # noqa: E402
from fastmcp.client.messages import MessageHandler  # noqa: E402
from fastmcp.exceptions import ToolError  # noqa: E402

from main import _build_instructions, create_mcp_server  # noqa: E402
from services.registry import (  # noqa: E402
    DEFAULT_ENABLED_GROUPS,
    TOOL_GROUPS,
    get_registered_tools,
)

OPTIONAL_GROUPS = sorted(set(TOOL_GROUPS) - DEFAULT_ENABLED_GROUPS)
DEFAULT_GROUPS = sorted(DEFAULT_ENABLED_GROUPS)
DOCS_TOOLS = {"unity_docs", "unity_reflect"}


class Recorder(MessageHandler):
    def __init__(self):
        self.tool_list_changed = 0

    async def on_tool_list_changed(self, message):
        self.tool_list_changed += 1


def unity_response(enabled_groups, prefs_version):
    """Simulate Unity's get_tool_states for the given enabled groups."""
    tools = []
    for tool in get_registered_tools():
        group = tool["group"]
        if group is None:
            continue  # server meta-tools do not exist Unity-side
        tools.append({
            "name": tool["name"],
            "group": group,
            "enabled": group in enabled_groups,
            "default_enabled": group in DEFAULT_ENABLED_GROUPS,
            "is_built_in": True,
            "description": tool["name"],
            "parameters": [],
        })
    data = {"tools": tools, "groups": []}
    if prefs_version is not None:
        data["preferences_version"] = prefs_version
    return {"data": data}


def expected_visible(extra_groups=()):
    allowed = set(DEFAULT_ENABLED_GROUPS) | set(extra_groups)
    return {
        t["name"] for t in get_registered_tools()
        if t["group"] is None or t["group"] in allowed
    }


async def sync_from(response):
    from services.tools import sync_tool_visibility_from_unity
    with patch(
        "transport.legacy.unity_connection.async_send_command_with_retry",
        new_callable=AsyncMock,
        return_value=response,
    ):
        return await sync_tool_visibility_from_unity(notify=True)


async def list_tool_names(client):
    return {t.name for t in await client.list_tools()}


async def scenario_fresh_v2(server):
    """Fresh v2 preferences (defaults only) must keep optional groups hidden."""
    recorder = Recorder()
    async with Client(server, message_handler=recorder) as client:
        result = await sync_from(
            unity_response(DEFAULT_ENABLED_GROUPS, prefs_version=2))
        assert result.get("synced") is True, result
        assert result["enabled_groups"] == DEFAULT_GROUPS, result
        assert result["skipped_legacy_groups"] == [], result
        assert set(result["disabled_groups"]) == set(OPTIONAL_GROUPS), result

        tools = await client.list_tools()
        visible = {t.name for t in tools}
        assert visible == expected_visible(), (
            f"unexpected initial tool set: extra={sorted(visible - expected_visible())} "
            f"missing={sorted(expected_visible() - visible)}"
        )
        assert DOCS_TOOLS.isdisjoint(visible), visible

        schema_bytes = sum(
            len(json.dumps(t.model_dump(), separators=(",", ":"))) for t in tools
        )
        print(
            f"STATS initial_tool_count={len(visible)} "
            f"schema_bytes={schema_bytes} approx_tokens={schema_bytes // 4}"
        )
        assert schema_bytes < 120_000, schema_bytes

        # Instructions are a minimal pointer; full workflow text stays in the
        # workflow resource, not in client-expanded instructions/descriptions.
        instructions = _build_instructions(project_scoped_tools=True)
        assert instructions == "Read mcpforunity://workflow.", instructions
        descriptions = "\n".join(t.description or "" for t in tools)
        for sentinel in (
            "pin session routing",        # workflow instance_routing text
            "dry_run and change_guard",   # workflow safe_mutation text
            "follow next_cursor until",   # workflow payload_sizing text
        ):
            assert sentinel not in descriptions, sentinel

        # Hidden docs tool is not callable...
        try:
            await client.call_tool("unity_reflect", {"action": "bogus"})
            raise AssertionError("unity_reflect callable while docs group hidden")
        except ToolError:
            pass

        # ...activating the group exposes it, notifies clients, and makes it callable.
        before = recorder.tool_list_changed
        await client.call_tool("manage_tools", {"action": "activate", "group": "docs"})
        visible = await list_tool_names(client)
        assert DOCS_TOOLS <= visible, visible
        assert recorder.tool_list_changed > before, "expected tools/list_changed"
        # unity_reflect validates its action before contacting Unity, so a bogus
        # action proves callability without a live editor.
        reflect = await client.call_tool("unity_reflect", {"action": "bogus"})
        assert reflect is not None

        await client.call_tool("manage_tools", {"action": "deactivate", "group": "docs"})
        visible = await list_tool_names(client)
        assert DOCS_TOOLS.isdisjoint(visible), visible

        await client.call_tool("manage_tools", {"action": "activate", "group": "docs"})
        await client.call_tool("manage_tools", {"action": "reset"})
        visible = await list_tool_names(client)
        assert visible == expected_visible(), "reset must restore default visibility"


async def scenario_legacy_all_enabled(server):
    """Legacy Unity data (no preferences_version, everything enabled) must not
    re-enable optional groups."""
    async with Client(server) as client:
        result = await sync_from(unity_response(set(TOOL_GROUPS), prefs_version=None))
        assert result.get("synced") is True, result
        assert result["enabled_groups"] == DEFAULT_GROUPS, result
        assert result["skipped_legacy_groups"] == OPTIONAL_GROUPS, result

        visible = await list_tool_names(client)
        assert visible == expected_visible(), (
            "legacy all-enabled state must not expand visibility: "
            f"extra={sorted(visible - expected_visible())}"
        )


async def scenario_v2_docs_persisted(server):
    """An explicit persisted optional-group choice (v2) is restored intentionally
    and reported accurately."""
    async with Client(server) as client:
        result = await sync_from(
            unity_response(DEFAULT_ENABLED_GROUPS | {"docs"}, prefs_version=2))
        assert result.get("synced") is True, result
        assert result["enabled_groups"] == sorted(DEFAULT_GROUPS + ["docs"]), result

        visible = await list_tool_names(client)
        assert visible == expected_visible(["docs"]), visible

        # tool-groups resource reports effective state and its source.
        content = await client.read_resource("mcpforunity://tool-groups")
        payload = json.loads(content[0].text)
        by_name = {g["name"]: g for g in payload["groups"]}
        assert by_name["docs"]["enabled"] is True, by_name["docs"]
        assert by_name["docs"]["source"] == "unity", by_name["docs"]
        assert by_name["docs"]["unity_persisted"] is True, by_name["docs"]
        assert by_name["docs"]["default_enabled"] is False, by_name["docs"]
        assert by_name["core"]["enabled"] is True, by_name["core"]
        assert by_name["vfx"]["enabled"] is False, by_name["vfx"]
        assert by_name["vfx"]["unity_persisted"] is False, by_name["vfx"]

        # A session override beats the Unity-persisted state and is reported as such.
        await client.call_tool("manage_tools", {"action": "deactivate", "group": "docs"})
        visible = await list_tool_names(client)
        assert DOCS_TOOLS.isdisjoint(visible), visible
        groups = await client.call_tool("manage_tools", {"action": "list_groups"})
        listed = {g["name"]: g for g in groups.structured_content["data"]["groups"]}
        assert listed["docs"]["enabled"] is False, listed["docs"]
        assert listed["docs"]["source"] == "session", listed["docs"]

        # Session reset falls back to the intentional Unity-persisted state.
        await client.call_tool("manage_tools", {"action": "reset"})
        visible = await list_tool_names(client)
        assert visible == expected_visible(["docs"]), visible


async def main():
    server = create_mcp_server(project_scoped_tools=True)
    await scenario_fresh_v2(server)
    await scenario_legacy_all_enabled(server)
    await scenario_v2_docs_persisted(server)
    print("OK")


if __name__ == "__main__":
    asyncio.run(main())
