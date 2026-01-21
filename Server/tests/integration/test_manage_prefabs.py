import pytest

from .test_helpers import DummyContext
import services.tools.manage_prefabs as manage_prefabs_mod


@pytest.mark.asyncio
async def test_manage_prefabs_create_from_gameobject_target_path_infers_search_method(monkeypatch):
    captured = {}

    async def fake_send(cmd, params, **kwargs):
        captured["cmd"] = cmd
        captured["params"] = params
        return {"success": True, "data": {}}

    monkeypatch.setattr(manage_prefabs_mod, "async_send_command_with_retry", fake_send)

    resp = await manage_prefabs_mod.manage_prefabs(
        ctx=DummyContext(),
        action="create_from_gameobject",
        target={"path": "/Canvas/Panel"},
        prefab_path="Assets/Temp/Test.prefab",
    )

    assert resp.get("success") is True
    assert captured["cmd"] == "manage_prefabs"
    assert captured["params"]["action"] == "create_from_gameobject"
    assert captured["params"]["target"] == "Canvas/Panel"
    assert captured["params"]["searchMethod"] == "by_path"
    assert captured["params"]["prefabPath"] == "Assets/Temp/Test.prefab"


@pytest.mark.asyncio
async def test_manage_prefabs_create_from_gameobject_target_instance_id_infers_search_method(monkeypatch):
    captured = {}

    async def fake_send(cmd, params, **kwargs):
        captured["params"] = params
        return {"success": True, "data": {}}

    monkeypatch.setattr(manage_prefabs_mod, "async_send_command_with_retry", fake_send)

    resp = await manage_prefabs_mod.manage_prefabs(
        ctx=DummyContext(),
        action="create_from_gameobject",
        target={"instanceID": 12345},
        prefab_path="Assets/Temp/Test.prefab",
    )

    assert resp.get("success") is True
    assert captured["params"]["target"] == 12345
    assert captured["params"]["searchMethod"] == "by_id"
