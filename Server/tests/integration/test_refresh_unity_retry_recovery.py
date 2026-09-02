import time

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
    states = [
        {"data": {"update_tick": 10, "compilation": {"last_compile_started_unix_ms": 100,
                                                       "last_domain_reload_after_unix_ms": 500},
                  "advice": {"blocking_reasons": []}}},
        {"data": {"update_tick": 11, "compilation": {"is_compiling": True,
                                                       "last_compile_started_unix_ms": 550,
                                                       "last_domain_reload_after_unix_ms": 500},
                  "advice": {"blocking_reasons": ["compiling"]}}},
        # Idle gap between compile-finish and domain-reload-start: must NOT count as ready.
        {"data": {"update_tick": 12, "compilation": {"last_compile_started_unix_ms": 550,
                                                       "last_compile_errors": 0,
                                                       "last_compile_warnings": 2,
                                                       "last_compile_duration_seconds": 1.25,
                                                       "last_domain_reload_after_unix_ms": 500},
                  "advice": {"blocking_reasons": []}}},
        {"data": {"update_tick": 13, "compilation": {"last_compile_started_unix_ms": 550,
                                                       "last_compile_errors": 0,
                                                       "last_compile_warnings": 2,
                                                       "last_compile_duration_seconds": 1.25,
                                                       "last_domain_reload_after_unix_ms": 600},
                  "advice": {"blocking_reasons": []}}},
        {"data": {"update_tick": 14, "compilation": {"last_compile_started_unix_ms": 550,
                                                       "last_compile_errors": 0,
                                                       "last_compile_warnings": 2,
                                                       "last_compile_duration_seconds": 1.25,
                                                       "last_domain_reload_after_unix_ms": 600},
                  "advice": {"blocking_reasons": []}}},
    ]
    poll_index = 0

    async def fake_state(ctx):
        nonlocal poll_index
        state = states[min(poll_index, len(states) - 1)]
        poll_index += 1
        return state

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


@pytest.mark.asyncio
async def test_acknowledged_no_wait_compile_returns_resumable_job(monkeypatch):
    import services.tools.refresh_unity as refresh_mod

    refresh_mod._REFRESH_JOBS.clear()

    async def fake_send(send_fn, unity_instance, command_type, params, **kwargs):
        return {"success": True, "message": "Refresh requested.",
                "data": {"resulting_state": "compiling"}}

    monkeypatch.setattr(refresh_mod.unity_transport, "send_with_unity_instance", fake_send)

    response = await refresh_mod.refresh_unity(
        DummyContext(), compile="request", wait_for_ready=False,
    )
    payload = response.model_dump()
    job_id = payload["data"]["job_id"]

    assert payload["data"]["status"] == "running"
    assert payload["data"]["recovered_from_disconnect"] is False
    assert job_id in refresh_mod._REFRESH_JOBS


@pytest.mark.asyncio
async def test_no_wait_compile_does_not_acknowledge_exhausted_preflight(monkeypatch):
    import services.tools.refresh_unity as refresh_mod

    refresh_mod._REFRESH_JOBS.clear()

    async def fake_send(send_fn, unity_instance, command_type, params, **kwargs):
        return MCPResponse(
            success=False,
            error="Unity is reloading; please retry",
            hint="retry",
            data={"reason": "reloading", "stage": "preflight"},
        )

    monkeypatch.setattr(refresh_mod.unity_transport, "send_with_unity_instance", fake_send)

    response = await refresh_mod.refresh_unity(
        DummyContext(), compile="request", wait_for_ready=False,
    )
    payload = response.model_dump()

    assert payload["success"] is False
    assert payload["data"]["stage"] == "preflight"
    assert refresh_mod._REFRESH_JOBS == {}


@pytest.mark.asyncio
async def test_waiting_compile_does_not_create_job_for_exhausted_preflight(monkeypatch):
    import services.tools.refresh_unity as refresh_mod

    refresh_mod._REFRESH_JOBS.clear()

    async def fake_send(send_fn, unity_instance, command_type, params, **kwargs):
        return MCPResponse(
            success=False,
            error="Unity is reloading; please retry",
            hint="retry",
            data={"reason": "reloading", "stage": "preflight"},
        )

    async def fail_if_polled(*args, **kwargs):
        raise AssertionError("An unsent refresh must not create or poll a resumable job")

    monkeypatch.setattr(refresh_mod.unity_transport, "send_with_unity_instance", fake_send)
    monkeypatch.setattr(refresh_mod, "wait_for_editor_ready", fail_if_polled)

    response = await refresh_mod.refresh_unity(
        DummyContext(), compile="request", wait_for_ready=True,
    )
    payload = response.model_dump()

    assert payload["success"] is False
    assert payload["hint"] == "retry"
    assert payload["data"] == {"reason": "reloading", "stage": "preflight"}
    assert refresh_mod._REFRESH_JOBS == {}


@pytest.mark.asyncio
async def test_expired_refresh_job_is_removed_before_resume(monkeypatch):
    import services.tools.refresh_unity as refresh_mod

    refresh_mod._REFRESH_JOBS.clear()
    refresh_mod._REFRESH_JOBS["expired-job"] = {
        "unity_instance": "ProjectA@111",
        "baseline_compile_started_ms": 100,
        "compile_requested": True,
        "created_at": time.monotonic() - refresh_mod._REFRESH_JOB_TTL_SECONDS,
    }

    async def fail_if_polled(*args, **kwargs):
        raise AssertionError("An expired refresh job must not poll editor state")

    monkeypatch.setattr(refresh_mod, "wait_for_editor_ready", fail_if_polled)

    response = await refresh_mod.refresh_unity(DummyContext(), job_id="expired-job")

    assert response.model_dump()["error"] == "REFRESH_JOB_NOT_FOUND"
    assert "expired-job" not in refresh_mod._REFRESH_JOBS


def test_refresh_job_registry_evicts_oldest_entries_at_capacity(monkeypatch):
    import services.tools.refresh_unity as refresh_mod

    refresh_mod._REFRESH_JOBS.clear()
    monkeypatch.setattr(refresh_mod, "_MAX_REFRESH_JOBS", 2)
    timestamps = iter([1.0] * 3 + [2.0] * 3 + [3.0] * 3)
    monkeypatch.setattr(refresh_mod.time, "monotonic", lambda: next(timestamps))

    refresh_mod._register_refresh_job("oldest", {})
    refresh_mod._register_refresh_job("middle", {})
    refresh_mod._register_refresh_job("newest", {})

    assert list(refresh_mod._REFRESH_JOBS) == ["middle", "newest"]


@pytest.mark.asyncio
async def test_resumed_refresh_stays_bound_to_originating_unity_instance(monkeypatch):
    import services.tools.refresh_unity as refresh_mod

    refresh_mod._REFRESH_JOBS.clear()
    ctx = DummyContext()
    await ctx.set_state("unity_instance", "ProjectA@111")

    async def fake_send(send_fn, unity_instance, command_type, params, **kwargs):
        assert unity_instance == "ProjectA@111"
        return {"success": False, "error": "disconnected"}

    monkeypatch.setattr(refresh_mod.unity_transport, "send_with_unity_instance", fake_send)
    started = await refresh_mod.refresh_unity(ctx, compile="request", wait_for_ready=False)
    job_id = started.model_dump()["data"]["job_id"]
    assert refresh_mod._REFRESH_JOBS[job_id]["unity_instance"] == "ProjectA@111"

    await ctx.set_state("unity_instance", "ProjectB@222")
    observed_instances = []
    tick = 0

    async def fake_state(poll_ctx):
        nonlocal tick
        tick += 1
        observed_instances.append(await poll_ctx.get_state("unity_instance"))
        return {
            "data": {
                "update_tick": tick,
                "compilation": {
                    "last_compile_started_unix_ms": int(time.time() * 1000),
                    "last_compile_errors": 0,
                    "last_domain_reload_after_unix_ms": int(time.time() * 1000) + 10_000,
                },
                "advice": {"blocking_reasons": []},
            }
        }

    async def no_sleep(_seconds):
        return None

    monkeypatch.setattr(refresh_mod.editor_state, "get_editor_state", fake_state)
    monkeypatch.setattr(refresh_mod.asyncio, "sleep", no_sleep)

    resumed = await refresh_mod.refresh_unity(ctx, job_id=job_id)

    assert resumed.model_dump()["data"]["status"] == "succeeded"
    assert observed_instances == ["ProjectA@111", "ProjectA@111"]
    assert job_id not in refresh_mod._REFRESH_JOBS


@pytest.mark.asyncio
async def test_failed_compile_returns_errors_inline(monkeypatch):
    """A red compile must name what broke; a second read_console call should not be needed."""
    import services.tools.refresh_unity as refresh_mod

    details = [
        {"file": "Assets/Scripts/Player.cs", "line": 42, "column": 9,
         "message": "error CS1002: ; expected"},
        {"file": "Assets/Scripts/Enemy.cs", "line": 7, "column": 1,
         "message": "error CS0246: The type or namespace name 'Foo' could not be found"},
    ]
    compilation = {
        "last_compile_started_unix_ms": 550,
        "last_compile_errors": 2,
        "last_compile_warnings": 0,
        "last_compile_duration_seconds": 0.5,
        "last_compile_error_details": details,
        "last_domain_reload_after_unix_ms": 500,
    }
    states = [
        {"data": {"update_tick": 10,
                  "compilation": {"last_compile_started_unix_ms": 100,
                                  "last_domain_reload_after_unix_ms": 500},
                  "advice": {"blocking_reasons": []}}},
        {"data": {"update_tick": 11,
                  "compilation": {**compilation, "is_compiling": True},
                  "advice": {"blocking_reasons": ["compiling"]}}},
        # A compile that ends in errors never domain-reloads, so readiness resolves here.
        {"data": {"update_tick": 12, "compilation": compilation, "advice": {"blocking_reasons": []}}},
        {"data": {"update_tick": 13, "compilation": compilation, "advice": {"blocking_reasons": []}}},
    ]
    poll_index = 0

    async def fake_state(ctx):
        nonlocal poll_index
        state = states[min(poll_index, len(states) - 1)]
        poll_index += 1
        return state

    async def fake_send(send_fn, unity_instance, command_type, params, **kwargs):
        return {"success": True, "message": "Refresh requested.", "data": {"resulting_state": "compiling"}}

    monkeypatch.setattr(refresh_mod.editor_state, "get_editor_state", fake_state)
    monkeypatch.setattr(refresh_mod.unity_transport, "send_with_unity_instance", fake_send)

    payload = (await refresh_mod.refresh_unity(
        DummyContext(), compile="request", wait_for_ready=True)).model_dump()

    assert payload["success"] is False
    assert payload["error"] == "COMPILE_FAILED"
    assert payload["data"]["summary"]["error_details"] == details
    assert "Assets/Scripts/Player.cs(42,9): error CS1002: ; expected" in payload["message"]
    assert "Assets/Scripts/Enemy.cs(7,1)" in payload["message"]


def test_compile_error_digest_caps_at_three_and_counts_the_rest():
    from services.tools.refresh_unity import format_compile_errors

    details = [{"file": f"A{i}.cs", "line": i, "column": 1, "message": "boom"} for i in range(1, 6)]
    digest = format_compile_errors(details, total=9)

    assert digest.startswith("A1.cs(1,1): boom; A2.cs(2,1): boom; A3.cs(3,1): boom")
    assert digest.endswith("(+6 more)")
    assert "A4.cs" not in digest
