import pytest

from .test_helpers import DummyContext
import services.tools.manage_scene as manage_scene_mod


@pytest.mark.asyncio
async def test_manage_scene_get_hierarchy_paging_params_pass_through(monkeypatch):
    captured = {}

    async def fake_send(cmd, params, **kwargs):
        captured["params"] = params
        return {"success": True, "data": {}}

    monkeypatch.setattr(
        manage_scene_mod,
        "async_send_command_with_retry",
        fake_send,
    )

    resp = await manage_scene_mod.manage_scene(
        ctx=DummyContext(),
        action="get_hierarchy",
        parent="Player",
        page_size="10",
        cursor="20",
        max_nodes="1000",
        max_depth="6",
        max_children_per_node="200",
        include_transform="true",
    )

    assert resp.get("success") is True
    p = captured["params"]
    assert p["action"] == "get_hierarchy"
    assert p["parent"] == "Player"
    assert p["pageSize"] in (10, "10")
    assert p["cursor"] in (20, "20")
    assert p["maxNodes"] in (1000, "1000")
    assert p["maxDepth"] in (6, "6")
    assert p["maxChildrenPerNode"] in (200, "200")
    assert p["includeTransform"] in (True, "true")


@pytest.mark.asyncio
async def test_manage_scene_screenshot_preview_params_pass_through(monkeypatch):
    captured = {}

    async def fake_send(cmd, params, **kwargs):
        captured["params"] = params
        return {"success": True, "data": {}}

    monkeypatch.setattr(
        manage_scene_mod,
        "async_send_command_with_retry",
        fake_send,
    )

    resp = await manage_scene_mod.manage_scene(
        ctx=DummyContext(),
        action="screenshot_with_preview",
        screenshot_file_name="shot",
        screenshot_super_size="2",
        screenshot_width="640",
        screenshot_height="360",
        screenshot_return_mode="both",
        screenshot_preview_max_width="800",
        screenshot_preview_max_height="450",
        screenshot_preview_format="jpg",
        screenshot_preview_jpeg_quality="65",
        screenshot_preview_max_pixels="360000",
        screenshot_wait_for_write="true",
        screenshot_timeout_ms="7000",
    )

    assert resp.get("success") is True
    p = captured["params"]
    assert p["action"] == "screenshot_with_preview"
    assert p["fileName"] == "shot"
    assert p["superSize"] in (2, "2")
    assert p["width"] in (640, "640")
    assert p["height"] in (360, "360")
    assert p["returnMode"] == "both"
    assert p["previewMaxWidth"] in (800, "800")
    assert p["previewMaxHeight"] in (450, "450")
    assert p["previewFormat"] == "jpg"
    assert p["previewJpegQuality"] in (65, "65")
    assert p["previewMaxPixels"] in (360000, "360000")
    assert p["waitForWrite"] in (True, "true")
    assert p["timeoutMs"] in (7000, "7000")


@pytest.mark.asyncio
async def test_manage_scene_screenshot_dimensions_pass_through(monkeypatch):
    captured = {}

    async def fake_send(cmd, params, **kwargs):
        captured["params"] = params
        return {"success": True, "data": {}}

    monkeypatch.setattr(
        manage_scene_mod,
        "async_send_command_with_retry",
        fake_send,
    )

    resp = await manage_scene_mod.manage_scene(
        ctx=DummyContext(),
        action="screenshot",
        screenshot_width="320",
        screenshot_height="180",
        screenshot_wait_for_write="true",
    )

    assert resp.get("success") is True
    p = captured["params"]
    assert p["action"] == "screenshot"
    assert p["width"] in (320, "320")
    assert p["height"] in (180, "180")
    assert p["waitForWrite"] in (True, "true")


