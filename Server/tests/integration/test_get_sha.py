import pytest

from .test_helpers import DummyContext, setup_script_tools


@pytest.mark.asyncio
async def test_get_sha_param_shape_and_routing(monkeypatch):
    tools = setup_script_tools()
    get_sha = tools["get_sha"]

    captured = {}

    async def fake_send(cmd, params, **kwargs):
        captured["cmd"] = cmd
        captured["params"] = params
        return {"success": True, "data": {"sha256": "abc", "lengthBytes": 1, "lineEnding": "crlf", "bom": False, "lastModifiedUtc": "2020-01-01T00:00:00Z", "uri": "mcpforunity://path/Assets/Scripts/A.cs", "path": "Assets/Scripts/A.cs"}}

    # Patch the send_command_with_retry function at the module level where it's imported
    import transport.legacy.unity_connection
    monkeypatch.setattr(
        transport.legacy.unity_connection,
        "async_send_command_with_retry",
        fake_send,
    )
    # No need to patch tools.manage_script; it now calls unity_connection.send_command_with_retry

    resp = await get_sha(DummyContext(), uri="mcpforunity://path/Assets/Scripts/A.cs")
    assert captured["cmd"] == "manage_script"
    assert captured["params"]["action"] == "get_sha"
    assert captured["params"]["name"] == "A"
    assert captured["params"]["path"].endswith("Assets/Scripts")
    assert resp["success"] is True
    # lineEnding/bom ride along: the caller is about to splice text into this file, and
    # apply_text_edits hands it back in the same shape.
    assert resp["data"] == {
        "sha256": "abc", "lengthBytes": 1, "lineEnding": "crlf", "bom": False}


@pytest.mark.asyncio
async def test_get_sha_preserves_queue_metadata_after_shaping(monkeypatch):
    get_sha = setup_script_tools()["get_sha"]
    queue = {"waited_ms": 2100, "reason": "compiling"}

    async def fake_send(*args, **kwargs):
        return {
            "success": True,
            "data": {
                "sha256": "abc",
                "lengthBytes": 1,
                "lineEnding": "lf",
                "bom": False,
            },
            "queue": queue,
        }

    import transport.legacy.unity_connection
    monkeypatch.setattr(
        transport.legacy.unity_connection,
        "async_send_command_with_retry",
        fake_send,
    )

    resp = await get_sha(DummyContext(), uri="Assets/Scripts/A.cs")

    assert resp["queue"] == queue
