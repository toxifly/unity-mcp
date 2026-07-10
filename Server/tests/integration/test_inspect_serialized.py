"""Registration and request-shaping tests for inspect_serialized."""

import asyncio

import services.tools.inspect_serialized as inspect_mod
from services.registry import get_registered_tools


def run(coro):
    return asyncio.run(coro)


def test_inspect_serialized_is_always_registered_in_core_group():
    tool = next(item for item in get_registered_tools() if item["name"] == "inspect_serialized")
    assert tool["group"] == "core"
    assert tool["unity_target"] == "inspect_serialized"


def test_inspect_serialized_shapes_whitelist_and_provenance_request(monkeypatch):
    captured = {}

    async def fake_instance(_ctx):
        return "project@instance"

    async def fake_send(_sender, instance, command, params):
        captured.update(instance=instance, command=command, params=params)
        return {"success": True, "data": {"findings": []}}

    monkeypatch.setattr(inspect_mod, "get_unity_instance_from_context", fake_instance)
    monkeypatch.setattr(inspect_mod, "send_with_unity_instance", fake_send)

    result = run(inspect_mod.inspect_serialized(
        object(),
        targets=[{"target": "Canvas/SelectionManager", "component": "SelectionManager"}],
        properties="outsideClickCatcher",
        include_prefab_provenance=True,
        cursor="25",
        page_size=25,
    ))

    assert result["success"] is True
    assert captured == {
        "instance": "project@instance",
        "command": "inspect_serialized",
        "params": {
            "targets": [{"target": "Canvas/SelectionManager", "component": "SelectionManager"}],
            "properties": ["outsideClickCatcher"],
            "includePrefabProvenance": True,
            "includeMissingReferences": True,
            "includeInactive": True,
            "pageSize": 25,
            "cursor": "25",
        },
    }


def test_inspect_serialized_requires_targets_and_property_whitelist():
    assert run(inspect_mod.inspect_serialized(object(), [], ["value"]))["success"] is False
    assert run(inspect_mod.inspect_serialized(object(), ["Target"], []))["success"] is False
