"""
Tests for intentional tool-visibility syncing from Unity.

Unity preferences v2+ report group-based enabled states that the server may
trust; legacy (unversioned) data treated every built-in tool as enabled and
must never re-enable optional groups. Covers the shared server-level sync
core (used by both the HTTP register_tools path and the stdio startup sync),
the version gate, and effective-state reporting.
"""
from unittest.mock import AsyncMock, patch

import pytest

from services.registry import (
    DEFAULT_ENABLED_GROUPS,
    TOOL_GROUPS,
    get_group_visibility_states,
    parse_session_group_overrides,
)
from transport.models import RegisterToolsMessage
from transport.plugin_hub import PluginHub


class FakeMCP:
    """Minimal stand-in for FastMCP visibility transforms."""

    def __init__(self):
        self._transforms = []

    def enable(self, tags=None, components=None):
        self._transforms.append(("enable", frozenset(tags or ())))

    def disable(self, tags=None, components=None):
        self._transforms.append(("disable", frozenset(tags or ())))

    def calls(self, kind):
        return [tags for op, tags in self._transforms if op == kind]


GROUP_TOOLS = {
    **{name: [] for name in TOOL_GROUPS},
    "core": ["manage_scene"],
    "docs": ["unity_docs", "unity_reflect"],
    "vfx": ["manage_vfx"],
}


@pytest.fixture
def fake_mcp(monkeypatch):
    fake = FakeMCP()
    monkeypatch.setattr(PluginHub, "_mcp", fake)
    monkeypatch.setattr(PluginHub, "_unity_transform_start", None)
    monkeypatch.setattr(PluginHub, "_last_unity_sync", None)
    monkeypatch.setattr(
        "services.registry.tool_registry.get_group_tool_names",
        lambda: GROUP_TOOLS,
    )
    monkeypatch.setattr(
        "services.registry.get_group_tool_names",
        lambda: GROUP_TOOLS,
    )
    return fake


# ---------------------------------------------------------------------------
# _sync_server_tool_visibility — the shared core for HTTP and stdio paths
# ---------------------------------------------------------------------------

def test_trusted_sync_enables_optional_group(fake_mcp):
    tools = [{"name": "manage_scene"}, {"name": "unity_docs"}]

    summary = PluginHub._sync_server_tool_visibility(tools, trusted=True)

    assert summary is not None
    assert summary["enabled_groups"] == ["core", "docs"]
    assert summary["skipped_groups"] == []
    assert frozenset({"group:docs"}) in fake_mcp.calls("enable")

    last = PluginHub.get_last_unity_sync()
    assert last["trusted"] is True
    assert last["group_state"]["docs"] is True
    assert last["group_state"]["vfx"] is False


def test_untrusted_sync_never_enables_optional_group(fake_mcp):
    # Legacy Unity claims every tool is enabled — the v1 blanket default.
    tools = [{"name": "manage_scene"}, {"name": "unity_docs"}, {"name": "manage_vfx"}]

    summary = PluginHub._sync_server_tool_visibility(tools, trusted=False)

    assert summary is not None
    assert summary["enabled_groups"] == ["core"]
    assert set(summary["skipped_groups"]) == {"docs", "vfx"}
    assert frozenset({"group:docs"}) not in fake_mcp.calls("enable")
    assert frozenset({"group:vfx"}) not in fake_mcp.calls("enable")
    # Default-enabled groups still sync normally.
    assert frozenset({"group:core"}) in fake_mcp.calls("enable")

    last = PluginHub.get_last_unity_sync()
    assert last["trusted"] is False
    assert last["group_state"]["docs"] is False


def test_trusted_sync_disables_group_with_no_registered_tools(fake_mcp):
    # User disabled every docs tool in the Unity UI.
    tools = [{"name": "manage_scene"}]

    summary = PluginHub._sync_server_tool_visibility(tools, trusted=True)

    assert summary["enabled_groups"] == ["core"]
    assert frozenset({"group:docs"}) in fake_mcp.calls("disable")
    assert PluginHub.get_last_unity_sync()["group_state"]["docs"] is False


def test_resync_trims_previous_unity_transforms(fake_mcp):
    tools = [{"name": "manage_scene"}, {"name": "unity_docs"}]

    PluginHub._sync_server_tool_visibility(tools, trusted=True)
    first_len = len(fake_mcp._transforms)
    PluginHub._sync_server_tool_visibility(tools, trusted=True)

    assert len(fake_mcp._transforms) == first_len, (
        "re-sync must replace, not stack, Unity transforms"
    )


# ---------------------------------------------------------------------------
# stdio startup sync — version gate and consistency with the HTTP path
# ---------------------------------------------------------------------------

def _unity_state_response(prefs_version):
    tools = [
        {"name": "manage_scene", "group": "core", "enabled": True, "is_built_in": True},
        {"name": "unity_docs", "group": "docs", "enabled": True, "is_built_in": True},
        {"name": "manage_vfx", "group": "vfx", "enabled": True, "is_built_in": True},
    ]
    data = {"tools": tools, "groups": []}
    if prefs_version is not None:
        data["preferences_version"] = prefs_version
    return {"data": data}


@pytest.mark.asyncio
@pytest.mark.parametrize("prefs_version,expect_trusted", [(None, False), (1, False), (2, True)])
async def test_stdio_sync_derives_trust_from_preferences_version(
    prefs_version, expect_trusted,
):
    with patch(
        "transport.legacy.unity_connection.async_send_command_with_retry",
        new_callable=AsyncMock,
        return_value=_unity_state_response(prefs_version),
    ), patch(
        "transport.plugin_hub.PluginHub._sync_server_tool_visibility",
        return_value={"enabled_groups": [], "disabled_groups": [], "skipped_groups": []},
    ) as mock_sync, patch(
        "transport.plugin_hub.PluginHub._notify_mcp_tool_list_changed",
        new_callable=AsyncMock,
    ):
        from services.tools import sync_tool_visibility_from_unity
        result = await sync_tool_visibility_from_unity(notify=False)

    assert result["synced"] is True
    mock_sync.assert_called_once()
    assert mock_sync.call_args.kwargs["trusted"] is expect_trusted
    assert result["preferences_version"] == prefs_version


@pytest.mark.asyncio
async def test_stdio_and_http_paths_apply_the_same_state(fake_mcp):
    """The stdio startup sync and the HTTP register_tools path share the same
    core, so identical Unity data must yield identical effective state."""
    # stdio path: full sync from a simulated legacy get_tool_states response.
    with patch(
        "transport.legacy.unity_connection.async_send_command_with_retry",
        new_callable=AsyncMock,
        return_value=_unity_state_response(None),
    ), patch(
        "transport.plugin_hub.PluginHub._notify_mcp_tool_list_changed",
        new_callable=AsyncMock,
    ), patch(
        "services.custom_tool_service.CustomToolService.get_instance",
    ):
        from services.tools import sync_tool_visibility_from_unity
        stdio_result = await sync_tool_visibility_from_unity(notify=False)
    stdio_state = PluginHub.get_last_unity_sync()["group_state"]

    # HTTP path with the same (legacy → untrusted) tool list.
    PluginHub._unity_transform_start = None
    PluginHub._last_unity_sync = None
    PluginHub._sync_server_tool_visibility(
        [{"name": "manage_scene"}, {"name": "unity_docs"}, {"name": "manage_vfx"}],
        trusted=False,
    )
    http_state = PluginHub.get_last_unity_sync()["group_state"]

    assert stdio_state == http_state
    assert stdio_result["enabled_groups"] == ["core"]
    assert set(stdio_result["skipped_legacy_groups"]) == {"docs", "vfx"}


def test_register_tools_message_parses_preferences_version():
    assert RegisterToolsMessage(tools=[]).preferences_version is None
    msg = RegisterToolsMessage.model_validate(
        {"type": "register_tools", "tools": [], "preferences_version": 2}
    )
    assert msg.preferences_version == 2


# ---------------------------------------------------------------------------
# Effective-state reporting
# ---------------------------------------------------------------------------

def test_parse_session_group_overrides_last_rule_wins():
    rules = [
        {"tags": ["group:docs"], "enabled": True},
        {"tags": ["other-tag"], "enabled": False},
        {"tags": ["group:docs"], "enabled": False},
        {"tags": ["group:vfx"]},  # enabled defaults to True
    ]
    assert parse_session_group_overrides(rules) == {"docs": False, "vfx": True}
    assert parse_session_group_overrides(None) == {}


def test_group_visibility_states_precedence(monkeypatch):
    monkeypatch.setattr(PluginHub, "_last_unity_sync", {
        "group_state": {"docs": True, "vfx": False, "core": True},
        "trusted": True,
        "skipped_groups": [],
    })

    states = {g["name"]: g for g in get_group_visibility_states({"vfx": True})}

    # Session override wins over Unity-persisted state.
    assert states["vfx"]["enabled"] is True
    assert states["vfx"]["source"] == "session"
    assert states["vfx"]["unity_persisted"] is False
    # Unity-persisted state wins over declared defaults.
    assert states["docs"]["enabled"] is True
    assert states["docs"]["source"] == "unity"
    assert states["docs"]["default_enabled"] is False
    # No override, no Unity data → declared default.
    assert states["animation"]["enabled"] is False
    assert states["animation"]["source"] == "default"
    assert states["animation"]["unity_persisted"] is None
    assert states["core"]["default_enabled"] is True


def test_group_visibility_states_without_unity_sync(monkeypatch):
    monkeypatch.setattr(PluginHub, "_last_unity_sync", None)

    states = {g["name"]: g for g in get_group_visibility_states()}

    for name, state in states.items():
        assert state["source"] == "default"
        assert state["enabled"] == (name in DEFAULT_ENABLED_GROUPS)
        assert state["unity_persisted"] is None
