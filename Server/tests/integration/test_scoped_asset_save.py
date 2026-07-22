"""Registration and request-shaping tests for scoped asset saves."""

import asyncio

import services.tools.scoped_asset_save as scoped_mod
from services.registry import get_registered_tools


def run(coro):
    return asyncio.run(coro)


def test_all_scoped_save_tools_are_registered_in_core():
    registered = {item["name"]: item for item in get_registered_tools()}
    names = {
        "save_scene_scoped",
        "save_prefab_scoped",
        "save_assets_scoped",
        "preview_asset_changes",
    }
    assert names <= registered.keys()
    assert all(registered[name]["group"] == "core" for name in names)


def test_save_scene_scoped_shapes_explicit_scope(monkeypatch):
    captured = {}

    async def fake_send(ctx, command, params):
        captured.update(ctx=ctx, command=command, params=params)
        return {"success": True}

    monkeypatch.setattr(scoped_mod, "_send", fake_send)
    result = run(scoped_mod.save_scene_scoped(
        object(), scene_path="Assets/Scenes/Main.unity", dirty_scene_policy="allow"
    ))

    assert result["success"] is True
    assert captured["command"] == "save_scene_scoped"
    assert captured["params"] == {
        "scenePath": "Assets/Scenes/Main.unity",
        "dirtyScenePolicy": "allow",
        "unscopedChanges": "include",
    }


def test_save_scene_scoped_forwards_unscoped_changes_mode(monkeypatch):
    captured = {}

    async def fake_send(ctx, command, params):
        captured.update(command=command, params=params)
        return {"success": True}

    monkeypatch.setattr(scoped_mod, "_send", fake_send)
    result = run(scoped_mod.save_scene_scoped(
        object(), scene_name="Main", unscoped_changes="exclude"
    ))

    assert result["success"] is True
    assert captured["params"]["unscopedChanges"] == "exclude"
    assert captured["params"]["sceneName"] == "Main"


def test_save_assets_scoped_normalizes_one_path(monkeypatch):
    captured = {}

    async def fake_send(_ctx, command, params):
        captured.update(command=command, params=params)
        return {"success": True}

    monkeypatch.setattr(scoped_mod, "_send", fake_send)
    result = run(scoped_mod.save_assets_scoped(object(), "Assets/Data/Config.asset"))

    assert result["success"] is True
    assert captured == {
        "command": "save_assets_scoped",
        "params": {"assetPaths": ["Assets/Data/Config.asset"]},
    }


def test_preview_rejects_mixed_scene_and_asset_scopes():
    result = run(scoped_mod.preview_asset_changes(
        object(), asset_paths=["Assets/Data.asset"], scene_path="Assets/Main.unity"
    ))
    assert result["success"] is False


def test_preview_uses_non_refreshing_preflight(monkeypatch):
    captured = {}

    async def fake_send(_ctx, command, params, *, refresh_if_dirty=True):
        captured.update(
            command=command,
            params=params,
            refresh_if_dirty=refresh_if_dirty,
        )
        return {"success": True}

    monkeypatch.setattr(scoped_mod, "_send", fake_send)
    result = run(scoped_mod.preview_asset_changes(
        object(), asset_paths="Assets/Data/Config.asset"
    ))

    assert result["success"] is True
    assert captured == {
        "command": "preview_asset_changes",
        "params": {"assetPaths": ["Assets/Data/Config.asset"]},
        "refresh_if_dirty": False,
    }
