"""Tests for manage_editor tool."""
import asyncio
import inspect
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from services.tools.manage_editor import manage_editor
import services.tools.manage_editor as manage_editor_mod
from services.registry import get_registered_tools

# ── Fixture ──────────────────────────────────────────────────────────


@pytest.fixture
def mock_unity(monkeypatch):
    captured: dict[str, object] = {}

    async def fake_send(send_fn, unity_instance, tool_name, params):
        captured["unity_instance"] = unity_instance
        captured["tool_name"] = tool_name
        captured["params"] = params
        return {"success": True, "message": "ok"}

    monkeypatch.setattr(
        "services.tools.manage_editor.get_unity_instance_from_context",
        AsyncMock(return_value="unity-instance-1"),
    )
    monkeypatch.setattr(
        "services.tools.manage_editor.send_with_unity_instance",
        fake_send,
    )
    return captured


# ── Undo/Redo ────────────────────────────────────────────────────────


def test_undo_forwards_to_unity(mock_unity):
    result = asyncio.run(manage_editor(SimpleNamespace(), action="undo"))
    assert result["success"] is True
    assert mock_unity["params"]["action"] == "undo"
    assert mock_unity["tool_name"] == "manage_editor"


def test_redo_forwards_to_unity(mock_unity):
    result = asyncio.run(manage_editor(SimpleNamespace(), action="redo"))
    assert result["success"] is True
    assert mock_unity["params"]["action"] == "redo"


# ── All Unity-forwarded actions ──────────────────────────────────────

UNITY_FORWARDED_ACTIONS = [
    "play", "pause", "stop", "set_active_tool",
    "add_tag", "remove_tag", "add_layer", "remove_layer",
    "get_scripting_defines", "deploy_package", "restore_package",
    "undo", "redo",
]


@pytest.mark.parametrize("action_name", UNITY_FORWARDED_ACTIONS)
def test_every_action_forwards_to_unity(mock_unity, action_name):
    result = asyncio.run(manage_editor(SimpleNamespace(), action=action_name))
    assert result["success"] is True
    assert mock_unity["params"]["action"] == action_name


# ── Python-only actions ──────────────────────────────────────────────


def test_telemetry_status_handled_python_side(mock_unity):
    result = asyncio.run(manage_editor(SimpleNamespace(), action="telemetry_status"))
    assert result["success"] is True
    assert "telemetry_enabled" in result
    assert "params" not in mock_unity


def test_telemetry_ping_handled_python_side(mock_unity):
    result = asyncio.run(manage_editor(SimpleNamespace(), action="telemetry_ping"))
    assert result["success"] is True
    assert "params" not in mock_unity


# ── None params omitted ─────────────────────────────────────────────


def test_undo_omits_none_params(mock_unity):
    result = asyncio.run(manage_editor(SimpleNamespace(), action="undo"))
    assert result["success"] is True
    params = mock_unity["params"]
    assert "toolName" not in params
    assert "tagName" not in params
    assert "layerName" not in params




# ── Scripting defines ───────────────────────────────────────────────


@pytest.fixture
def mock_mutation(monkeypatch):
    """set_scripting_defines routes through send_mutation, which owns its own transport."""
    captured: dict[str, object] = {}

    async def fake_send(send_fn, unity_instance, tool_name, params, **kwargs):
        captured["tool_name"] = tool_name
        captured["params"] = params
        captured["kwargs"] = kwargs
        return {"success": True, "message": "ok", "data": {"defines": params.get("defines"), "changed": True}}

    monkeypatch.setattr(
        "services.tools.manage_editor.get_unity_instance_from_context",
        AsyncMock(return_value="unity-instance-1"),
    )
    import services.tools.refresh_unity as refresh_mod
    monkeypatch.setattr(refresh_mod.unity_transport,
                        "send_with_unity_instance", fake_send)
    return captured


def test_get_scripting_defines_forwards_target(mock_unity):
    result = asyncio.run(manage_editor(
        SimpleNamespace(), action="get_scripting_defines", target="android"))
    assert result["success"] is True
    assert mock_unity["params"] == {"action": "get_scripting_defines", "target": "android"}


def test_set_scripting_defines_requires_defines(mock_unity):
    result = asyncio.run(manage_editor(
        SimpleNamespace(), action="set_scripting_defines"))
    assert result["success"] is False
    assert "defines is required" in result["message"]
    assert "params" not in mock_unity


def test_set_scripting_defines_forwards_the_full_list(mock_mutation):
    result = asyncio.run(manage_editor(
        SimpleNamespace(), action="set_scripting_defines", defines=["A", "B"]))
    assert result["success"] is True
    assert mock_mutation["params"]["defines"] == ["A", "B"]
    # send_mutation must not re-send into a reload; that is what causes double compiles.
    assert mock_mutation["kwargs"]["retry_on_reload"] is False


def test_set_scripting_defines_unwraps_a_json_array_string(mock_mutation):
    asyncio.run(manage_editor(
        SimpleNamespace(), action="set_scripting_defines", defines='["A", "B"]'))
    assert mock_mutation["params"]["defines"] == ["A", "B"]


def test_set_scripting_defines_keeps_an_empty_list_so_clearing_works(mock_mutation):
    asyncio.run(manage_editor(
        SimpleNamespace(), action="set_scripting_defines", defines=[]))
    assert mock_mutation["params"]["defines"] == []
