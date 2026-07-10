"""Registration and request-shaping tests for inspect_provenance."""

import asyncio

import services.tools.inspect_provenance as provenance_mod
from services.registry import get_registered_tools


def run(coro):
    return asyncio.run(coro)


def test_inspect_provenance_is_always_registered_in_core_group():
    tool = next(item for item in get_registered_tools() if item["name"] == "inspect_provenance")
    assert tool["group"] == "core"
    assert tool["unity_target"] == "inspect_provenance"


def test_inspect_provenance_shapes_compact_bounded_request(monkeypatch):
    captured = {}

    async def fake_instance(_ctx):
        return "project@instance"

    async def fake_send(_sender, instance, command, params):
        captured.update(instance=instance, command=command, params=params)
        return {"success": True, "data": {"findings": []}}

    monkeypatch.setattr(provenance_mod, "get_unity_instance_from_context", fake_instance)
    monkeypatch.setattr(provenance_mod, "send_with_unity_instance", fake_send)

    result = run(provenance_mod.inspect_provenance(
        object(),
        targets="Canvas/NestedWidget",
        include_property_overrides=True,
        include_component_overrides=False,
        override_limit=25,
        page_size=10,
        cursor="10",
    ))

    assert result["success"] is True
    assert captured == {
        "instance": "project@instance",
        "command": "inspect_provenance",
        "params": {
            "targets": ["Canvas/NestedWidget"],
            "includePropertyOverrides": True,
            "includeComponentOverrides": False,
            "includeInactive": True,
            "overrideLimit": 25,
            "componentLimit": 10,
            "pageSize": 10,
            "cursor": "10",
        },
    }


def test_inspect_provenance_requires_non_empty_targets():
    assert run(provenance_mod.inspect_provenance(object(), []))["success"] is False
    assert run(provenance_mod.inspect_provenance(object(), [" "]))["success"] is False


def test_inspect_provenance_bounds_nested_override_payloads():
    result = run(provenance_mod.inspect_provenance(
        object(),
        ["Target"],
        page_size=50,
        override_limit=50,
    ))
    assert result["success"] is False
    assert "must not exceed 500" in result["message"]
