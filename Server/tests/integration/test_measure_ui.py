"""Registration and request-shaping tests for the measure_ui tool."""

import asyncio

import services.tools.measure_ui as measure_ui_mod
from services.registry import get_registered_tools


def run(coro):
    return asyncio.run(coro)


def test_measure_ui_is_always_registered_in_core_group():
    tool = next(item for item in get_registered_tools() if item["name"] == "measure_ui")
    assert tool["group"] == "core"
    assert tool["unity_target"] == "measure_ui"


def test_measure_ui_sends_explicit_space_and_assertions(monkeypatch):
    captured = {}

    async def fake_instance(_ctx):
        return "project@instance"

    async def fake_send(_sender, instance, command, params):
        captured.update(instance=instance, command=command, params=params)
        return {"success": True, "data": {"measurements": []}}

    monkeypatch.setattr(measure_ui_mod, "get_unity_instance_from_context", fake_instance)
    monkeypatch.setattr(measure_ui_mod, "send_with_unity_instance", fake_send)

    result = run(measure_ui_mod.measure_ui(
        object(),
        targets="Button",
        space="canvas",
        include_inactive=True,
        assertions=[{"type": "inside", "target": "Button", "container": "Canvas"}],
    ))

    assert result["success"] is True
    assert captured == {
        "instance": "project@instance",
        "command": "measure_ui",
        "params": {
            "space": "canvas",
            "targets": ["Button"],
            "includeInactive": True,
            "assertions": [{"type": "inside", "target": "Button", "container": "Canvas"}],
        },
    }


def test_measure_ui_requires_targets_or_container():
    result = run(measure_ui_mod.measure_ui(object()))
    assert result["success"] is False
