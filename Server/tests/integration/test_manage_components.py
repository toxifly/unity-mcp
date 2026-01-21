"""
Tests for the manage_components tool.

This tool handles component lifecycle operations (add, remove, set_property).
"""
import pytest

from .test_helpers import DummyContext
import services.tools.manage_components as manage_comp_mod


@pytest.mark.asyncio
async def test_manage_components_add_single(monkeypatch):
    """Test adding a single component."""
    captured = {}

    async def fake_send(cmd, params, **kwargs):
        captured["cmd"] = cmd
        captured["params"] = params
        return {
            "success": True,
            "data": {
                "addedComponents": [{"typeName": "UnityEngine.Rigidbody", "instanceID": 12345}]
            },
        }

    monkeypatch.setattr(
        manage_comp_mod,
        "async_send_command_with_retry",
        fake_send,
    )

    resp = await manage_comp_mod.manage_components(
        ctx=DummyContext(),
        action="add",
        target="Player",
        component_type="Rigidbody",
    )

    assert resp.get("success") is True
    assert captured["cmd"] == "manage_components"
    assert captured["params"]["action"] == "add"
    assert captured["params"]["target"] == "Player"
    assert captured["params"]["componentType"] == "Rigidbody"


@pytest.mark.asyncio
async def test_manage_components_remove(monkeypatch):
    """Test removing a component."""
    captured = {}

    async def fake_send(cmd, params, **kwargs):
        captured["params"] = params
        return {"success": True, "data": {"instanceID": 12345, "name": "Player"}}

    monkeypatch.setattr(
        manage_comp_mod,
        "async_send_command_with_retry",
        fake_send,
    )

    resp = await manage_comp_mod.manage_components(
        ctx=DummyContext(),
        action="remove",
        target="Player",
        component_type="Rigidbody",
    )

    assert resp.get("success") is True
    assert captured["params"]["action"] == "remove"
    assert captured["params"]["componentType"] == "Rigidbody"


@pytest.mark.asyncio
async def test_manage_components_set_property_single(monkeypatch):
    """Test setting a single component property."""
    captured = {}

    async def fake_send(cmd, params, **kwargs):
        captured["params"] = params
        return {"success": True, "data": {"instanceID": 12345}}

    monkeypatch.setattr(
        manage_comp_mod,
        "async_send_command_with_retry",
        fake_send,
    )

    resp = await manage_comp_mod.manage_components(
        ctx=DummyContext(),
        action="set_property",
        target="Player",
        component_type="Rigidbody",
        property="mass",
        value=5.0,
    )

    assert resp.get("success") is True
    assert captured["params"]["action"] == "set_property"
    assert captured["params"]["property"] == "mass"
    assert captured["params"]["value"] == 5.0


@pytest.mark.asyncio
async def test_manage_components_set_property_multiple(monkeypatch):
    """Test setting multiple component properties via properties dict."""
    captured = {}

    async def fake_send(cmd, params, **kwargs):
        captured["params"] = params
        return {"success": True, "data": {"instanceID": 12345}}

    monkeypatch.setattr(
        manage_comp_mod,
        "async_send_command_with_retry",
        fake_send,
    )

    resp = await manage_comp_mod.manage_components(
        ctx=DummyContext(),
        action="set_property",
        target="Player",
        component_type="Rigidbody",
        properties={"mass": 5.0, "drag": 0.5},
    )

    assert resp.get("success") is True
    assert captured["params"]["action"] == "set_property"
    assert captured["params"]["properties"] == {"mass": 5.0, "drag": 0.5}


@pytest.mark.asyncio
async def test_manage_components_set_property_json_string(monkeypatch):
    """Test setting component properties with JSON string input."""
    captured = {}

    async def fake_send(cmd, params, **kwargs):
        captured["params"] = params
        return {"success": True, "data": {"instanceID": 12345}}

    monkeypatch.setattr(
        manage_comp_mod,
        "async_send_command_with_retry",
        fake_send,
    )

    resp = await manage_comp_mod.manage_components(
        ctx=DummyContext(),
        action="set_property",
        target="Player",
        component_type="Rigidbody",
        properties='{"mass": 10.0}',  # JSON string
    )

    assert resp.get("success") is True
    assert captured["params"]["properties"] == {"mass": 10.0}


@pytest.mark.asyncio
async def test_manage_components_add_with_properties(monkeypatch):
    """Test adding a component with initial properties."""
    captured = {}

    async def fake_send(cmd, params, **kwargs):
        captured["params"] = params
        return {
            "success": True,
            "data": {"addedComponents": [{"typeName": "Rigidbody", "instanceID": 123}]},
        }

    monkeypatch.setattr(
        manage_comp_mod,
        "async_send_command_with_retry",
        fake_send,
    )

    resp = await manage_comp_mod.manage_components(
        ctx=DummyContext(),
        action="add",
        target="Player",
        component_type="Rigidbody",
        properties={"mass": 2.0, "useGravity": False},
    )

    assert resp.get("success") is True
    assert captured["params"]["properties"] == {"mass": 2.0, "useGravity": False}


@pytest.mark.asyncio
async def test_manage_components_search_method_passthrough(monkeypatch):
    """Test that search_method is correctly passed through."""
    captured = {}

    async def fake_send(cmd, params, **kwargs):
        captured["params"] = params
        return {"success": True, "data": {}}

    monkeypatch.setattr(
        manage_comp_mod,
        "async_send_command_with_retry",
        fake_send,
    )

    resp = await manage_comp_mod.manage_components(
        ctx=DummyContext(),
        action="add",
        target="Canvas/Panel",
        component_type="Image",
        search_method="by_path",
    )

    assert resp.get("success") is True
    assert captured["params"]["searchMethod"] == "by_path"


@pytest.mark.asyncio
async def test_manage_components_target_by_id(monkeypatch):
    """Test targeting by instance ID."""
    captured = {}

    async def fake_send(cmd, params, **kwargs):
        captured["params"] = params
        return {"success": True, "data": {}}

    monkeypatch.setattr(
        manage_comp_mod,
        "async_send_command_with_retry",
        fake_send,
    )

    resp = await manage_comp_mod.manage_components(
        ctx=DummyContext(),
        action="add",
        target=12345,  # Integer instance ID
        component_type="BoxCollider",
        search_method="by_id",
    )

    assert resp.get("success") is True
    assert captured["params"]["target"] == 12345
    assert captured["params"]["searchMethod"] == "by_id"


@pytest.mark.asyncio
async def test_manage_components_target_object_reference_infers_search_method(monkeypatch):
    """Test that {path|name|instanceID} target objects are normalized and inferred into searchMethod."""
    captured = {}

    async def fake_send(cmd, params, **kwargs):
        captured["cmd"] = cmd
        captured["params"] = params
        return {"success": True, "data": {}}

    monkeypatch.setattr(manage_comp_mod, "async_send_command_with_retry", fake_send)

    resp = await manage_comp_mod.manage_components(
        ctx=DummyContext(),
        action="add",
        target={"path": "/Canvas/Panel"},
        component_type="Image",
    )

    assert resp.get("success") is True
    assert captured["cmd"] == "manage_components"
    assert captured["params"]["target"] == "Canvas/Panel"
    assert captured["params"]["searchMethod"] == "by_path"


@pytest.mark.asyncio
async def test_manage_components_get_components_resolves_path(monkeypatch):
    """Test get_components action resolves a {path} target and calls get_gameobject_components."""
    calls = []

    async def fake_send(cmd, params, **kwargs):
        calls.append((cmd, params))
        if cmd == "find_gameobjects":
            assert params["searchMethod"] == "by_path"
            assert params["searchTerm"] == "Canvas/Panel"  # leading '/' stripped
            return {"success": True, "data": {"instanceIDs": [111]}}
        if cmd == "get_gameobject_components":
            assert params["instanceID"] == 111
            assert params["includeProperties"] is True
            return {"success": True, "data": {"gameObjectID": 111, "components": []}}
        return {"success": False, "message": f"Unexpected cmd: {cmd}"}

    monkeypatch.setattr(manage_comp_mod, "async_send_command_with_retry", fake_send)

    resp = await manage_comp_mod.manage_components(
        ctx=DummyContext(),
        action="get_components",
        target={"path": "/Canvas/Panel"},
    )

    assert resp.get("success") is True
    assert calls[0][0] == "find_gameobjects"
    assert calls[1][0] == "get_gameobject_components"


@pytest.mark.asyncio
async def test_manage_components_get_properties_filters(monkeypatch):
    """Test get_properties action returns only requested fields."""
    captured = {}

    async def fake_send(cmd, params, **kwargs):
        captured["cmd"] = cmd
        captured["params"] = params
        if cmd == "get_gameobject_component":
            return {
                "success": True,
                "data": {
                    "gameObjectID": 12345,
                    "gameObjectName": "Player",
                    "component": {"mass": 5.0, "drag": 0.5},
                },
            }
        return {"success": False, "message": f"Unexpected cmd: {cmd}"}

    monkeypatch.setattr(manage_comp_mod, "async_send_command_with_retry", fake_send)

    resp = await manage_comp_mod.manage_components(
        ctx=DummyContext(),
        action="get_properties",
        target=12345,
        component_type="Rigidbody",
        property_names="mass,missingField",
    )

    assert resp.get("success") is True
    assert captured["cmd"] == "get_gameobject_component"
    assert captured["params"]["instanceID"] == 12345
    assert captured["params"]["componentName"] == "Rigidbody"
    data = resp.get("data") or {}
    assert data.get("properties") == {"mass": 5.0}
    assert data.get("missing") == ["missingField"]

