import pytest

from models import MCPResponse
from services.state.external_changes_scanner import external_changes_scanner
from services.state.external_changes_scanner import ExternalChangesState

from .test_helpers import DummyContext


@pytest.mark.asyncio
async def test_refresh_unity_recovers_from_retry_disconnect(monkeypatch):
    """
    Option A: if Unity disconnects and the transport returns hint=retry, refresh_unity(wait_for_ready=true)
    should poll readiness and then return success + clear external dirty.
    """
    from services.tools.refresh_unity import refresh_unity

    ctx = DummyContext()
    await ctx.set_state("unity_instance", "UnityMCPTests@cc8756d4cce0805a")

    # Seed dirty state
    inst = "UnityMCPTests@cc8756d4cce0805a"
    external_changes_scanner._states[inst] = ExternalChangesState(dirty=True, dirty_since_unix_ms=1)

    async def fake_send_with_unity_instance(send_fn, unity_instance, command_type, params, **kwargs):
        if command_type == "refresh_unity":
            return {"success": False, "error": "disconnected", "hint": "retry"}
        elif command_type == "get_editor_state":
            return {"success": True, "data": {"advice": {"ready_for_tools": True}}}
        raise ValueError(f"Unexpected command: {command_type}")

    state_tick = 0

    async def fake_get_editor_state(ctx):
        nonlocal state_tick
        state_tick += 1
        return {"data": {"update_tick": state_tick, "advice": {"ready_for_tools": True}}}

    import services.tools.refresh_unity as refresh_mod
    monkeypatch.setattr(refresh_mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)
    monkeypatch.setattr(refresh_mod.editor_state, "get_editor_state", fake_get_editor_state)

    resp = await refresh_unity(ctx, wait_for_ready=True)
    payload = resp.model_dump() if hasattr(resp, "model_dump") else resp
    assert payload["success"] is True
    assert payload.get("data", {}).get("recovered_from_disconnect") is True

    # Dirty should be cleared
    assert external_changes_scanner._states[inst].dirty is False


@pytest.mark.asyncio
async def test_compile_refresh_returns_terminal_summary_after_stable_ready(monkeypatch):
    import services.tools.refresh_unity as refresh_mod

    sent_params = None
    states = iter([
        {"data": {"update_tick": 10, "compilation": {"last_compile_started_unix_ms": 100},
                  "advice": {"blocking_reasons": []}}},
        {"data": {"update_tick": 11, "compilation": {"is_compiling": True,
                                                       "last_compile_started_unix_ms": 200},
                  "advice": {"blocking_reasons": ["compiling"]}}},
        {"data": {"update_tick": 12, "compilation": {"last_compile_started_unix_ms": 200,
                                                       "last_compile_errors": 0,
                                                       "last_compile_warnings": 2,
                                                       "last_compile_duration_seconds": 1.25},
                  "advice": {"blocking_reasons": []}}},
        {"data": {"update_tick": 13, "compilation": {"last_compile_started_unix_ms": 200,
                                                       "last_compile_errors": 0,
                                                       "last_compile_warnings": 2,
                                                       "last_compile_duration_seconds": 1.25},
                  "advice": {"blocking_reasons": []}}},
    ])

    async def fake_state(ctx):
        return next(states)

    async def fake_send(send_fn, unity_instance, command_type, params, **kwargs):
        nonlocal sent_params
        sent_params = params
        return {"success": True, "message": "Refresh requested.", "data": {"resulting_state": "compiling"}}

    monkeypatch.setattr(refresh_mod.editor_state, "get_editor_state", fake_state)
    monkeypatch.setattr(refresh_mod.unity_transport, "send_with_unity_instance", fake_send)

    response = await refresh_mod.refresh_unity(DummyContext(), compile="request", wait_for_ready=True)
    payload = response.model_dump()

    assert sent_params["wait_for_ready"] is False
    assert payload["data"]["status"] == "succeeded"
    assert payload["data"]["resulting_state"] == "idle"
    assert payload["data"]["summary"] == {
        "compiled": True, "errors": 0, "warnings": 2, "duration_seconds": 1.25,
    }


