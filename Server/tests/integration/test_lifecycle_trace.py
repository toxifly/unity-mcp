"""Registration, validation, and request shaping for lifecycle_trace."""

import asyncio

import services.tools.lifecycle_trace as trace_mod
from services.registry import get_registered_tools


def run(coro):
    return asyncio.run(coro)


def test_lifecycle_trace_is_registered_in_core_group():
    tool = next(item for item in get_registered_tools() if item["name"] == "lifecycle_trace")
    assert tool["group"] == "core"
    assert tool["unity_target"] == "lifecycle_trace"


def test_start_shapes_bounded_trace_request(monkeypatch):
    captured = {}

    async def fake_instance(_ctx):
        return "project@instance"

    async def fake_send(_sender, instance, command, params):
        captured.update(instance=instance, command=command, params=params)
        return {"success": True, "data": {"session_id": "trace-1"}}

    monkeypatch.setattr(trace_mod, "get_unity_instance_from_context", fake_instance)
    monkeypatch.setattr(trace_mod, "send_with_unity_instance", fake_send)

    result = run(trace_mod.lifecycle_trace(
        object(), "start", targets="Canvas/Button",
        events=["OnEnable", "serialized_property_change"],
        property_whitelist=["UnityEngine.UI.Image.m_Color"],
        max_events=25, timeout_seconds=60,
    ))

    assert result["success"] is True
    assert captured == {
        "instance": "project@instance",
        "command": "lifecycle_trace",
        "params": {
            "action": "start", "maxEvents": 25, "timeoutSeconds": 60,
            "cursor": 0, "limit": 100, "targets": ["Canvas/Button"],
            "events": ["OnEnable", "serialized_property_change"],
            "propertyWhitelist": ["UnityEngine.UI.Image.m_Color"],
        },
    }


def test_poll_requires_session_and_forwards_cursor(monkeypatch):
    assert run(trace_mod.lifecycle_trace(object(), "poll"))["success"] is False

    captured = {}

    async def fake_instance(_ctx):
        return None

    async def fake_send(_sender, _instance, _command, params):
        captured.update(params)
        return {"success": True}

    monkeypatch.setattr(trace_mod, "get_unity_instance_from_context", fake_instance)
    monkeypatch.setattr(trace_mod, "send_with_unity_instance", fake_send)
    run(trace_mod.lifecycle_trace(object(), "poll", session_id="abc", cursor=12, limit=20))
    assert captured["sessionId"] == "abc"
    assert captured["cursor"] == 12
    assert captured["limit"] == 20


def test_start_bounds_targets_and_properties():
    assert run(trace_mod.lifecycle_trace(object(), "start", targets=[]))["success"] is False
    assert run(trace_mod.lifecycle_trace(object(), "start", targets=["x"] * 51))["success"] is False
    assert run(trace_mod.lifecycle_trace(
        object(), "start", targets=["x"], property_whitelist=["p"] * 101,
    ))["success"] is False
