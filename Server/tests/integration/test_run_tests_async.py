import asyncio
import time

import pytest

from .test_helpers import DummyContext


@pytest.mark.asyncio
async def test_run_tests_async_forwards_params(monkeypatch):
    from services.tools.run_tests import run_tests

    captured = {}

    async def fake_send_with_unity_instance(send_fn, unity_instance, command_type, params, **kwargs):
        captured["command_type"] = command_type
        captured["params"] = params
        return {"success": True, "data": {"job_id": "abc123", "status": "running", "mode": "EditMode"}}

    import services.tools.run_tests as mod
    monkeypatch.setattr(
        mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)

    resp = await run_tests(
        DummyContext(),
        mode="EditMode",
        test_names="MyNamespace.MyTests.TestA",
        include_details=True,
    )
    assert captured["command_type"] == "run_tests"
    assert captured["params"]["mode"] == "EditMode"
    assert captured["params"]["testNames"] == ["MyNamespace.MyTests.TestA"]
    assert captured["params"]["includeDetails"] is True
    assert resp.success is True
    assert resp.data is not None
    assert resp.data.job_id == "abc123"


@pytest.mark.asyncio
async def test_run_tests_forwards_init_timeout(monkeypatch):
    from services.tools.run_tests import run_tests

    captured = {}

    async def fake_send_with_unity_instance(send_fn, unity_instance, command_type, params, **kwargs):
        captured["params"] = params
        return {"success": True, "data": {"job_id": "abc123", "status": "running", "mode": "PlayMode"}}

    import services.tools.run_tests as mod
    monkeypatch.setattr(
        mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)

    resp = await run_tests(
        DummyContext(),
        mode="PlayMode",
        init_timeout=120000,
    )
    assert captured["params"]["initTimeout"] == 120000
    assert resp.success is True


@pytest.mark.asyncio
async def test_run_tests_omits_init_timeout_when_none(monkeypatch):
    from services.tools.run_tests import run_tests

    captured = {}

    async def fake_send_with_unity_instance(send_fn, unity_instance, command_type, params, **kwargs):
        captured["params"] = params
        return {"success": True, "data": {"job_id": "abc123", "status": "running", "mode": "EditMode"}}

    import services.tools.run_tests as mod
    monkeypatch.setattr(
        mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)

    resp = await run_tests(DummyContext(), mode="EditMode")
    assert "initTimeout" not in captured["params"]
    assert resp.success is True


@pytest.mark.asyncio
async def test_run_tests_preserves_queue_metadata_in_typed_start_response(monkeypatch):
    from services.tools.run_tests import RunTestsStartResponse, run_tests

    queue = {"waited_ms": 64000, "reason": "compiling"}

    async def fake_send_with_unity_instance(*args, **kwargs):
        return {
            "success": True,
            "data": {"job_id": "abc123", "status": "running", "mode": "EditMode"},
            "queue": queue,
        }

    import services.tools.run_tests as mod
    monkeypatch.setattr(
        mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance
    )

    resp = await run_tests(DummyContext(), mode="EditMode")

    assert isinstance(resp, RunTestsStartResponse)
    assert resp.model_dump()["queue"] == queue


@pytest.mark.asyncio
async def test_run_tests_rejects_negative_init_timeout():
    from services.tools.run_tests import run_tests

    resp = await run_tests(DummyContext(), mode="EditMode", init_timeout=-1)
    assert resp.success is False
    assert "init_timeout" in resp.error


@pytest.mark.asyncio
async def test_run_tests_rejects_zero_init_timeout():
    from services.tools.run_tests import run_tests

    resp = await run_tests(DummyContext(), mode="EditMode", init_timeout=0)
    assert resp.success is False
    assert "init_timeout" in resp.error


@pytest.mark.asyncio
async def test_get_test_job_forwards_job_id(monkeypatch):
    from services.tools.run_tests import get_test_job

    captured = {}

    async def fake_send_with_unity_instance(send_fn, unity_instance, command_type, params, **kwargs):
        captured["command_type"] = command_type
        captured["params"] = params
        return {"success": True, "data": {"job_id": params["job_id"], "status": "running", "mode": "EditMode"}}

    import services.tools.run_tests as mod
    monkeypatch.setattr(
        mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)

    resp = await get_test_job(DummyContext(), job_id="job-1")
    assert captured["command_type"] == "get_test_job"
    assert captured["params"]["job_id"] == "job-1"
    assert resp.success is True
    assert resp.data is not None
    assert resp.data.job_id == "job-1"


@pytest.mark.asyncio
async def test_get_test_job_preserves_queue_metadata_in_typed_response(monkeypatch):
    from services.tools.run_tests import GetTestJobResponse, get_test_job

    queue = {"waited_ms": 2100, "reason": "play_mode_transition"}

    async def fake_send_with_unity_instance(*args, **kwargs):
        return {
            "success": True,
            "data": {"job_id": "job-1", "status": "running", "mode": "PlayMode"},
            "queue": queue,
        }

    import services.tools.run_tests as mod
    monkeypatch.setattr(
        mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance
    )

    resp = await get_test_job(DummyContext(), job_id="job-1")

    assert isinstance(resp, GetTestJobResponse)
    assert resp.model_dump()["queue"] == queue


@pytest.mark.asyncio
async def test_terminal_test_job_preserves_direct_summary(monkeypatch):
    from services.tools.run_tests import get_test_job

    summary = {
        "total": 20,
        "passed": 18,
        "failed": 1,
        "skipped": 1,
        "duration_seconds": 11.7,
    }

    async def fake_send_with_unity_instance(*args, **kwargs):
        return {
            "success": True,
            "data": {"job_id": "job-1", "status": "succeeded", "summary": summary},
        }

    import services.tools.run_tests as mod
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)

    resp = await get_test_job(DummyContext(), job_id="job-1")

    assert resp.data.summary is not None
    assert resp.data.summary.model_dump() == summary


@pytest.mark.asyncio
async def test_terminal_test_job_recovers_summary_from_legacy_result(monkeypatch):
    from services.tools.run_tests import get_test_job

    async def fake_send_with_unity_instance(*args, **kwargs):
        return {
            "success": True,
            "data": {
                "job_id": "job-1",
                "status": "succeeded",
                "result": {
                    "mode": "EditMode",
                    "summary": {
                        "total": 3,
                        "passed": 2,
                        "failed": 0,
                        "skipped": 1,
                        "durationSeconds": 0.75,
                        "resultState": "Passed",
                    },
                },
            },
        }

    import services.tools.run_tests as mod
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)

    resp = await get_test_job(DummyContext(), job_id="job-1")

    assert resp.data.summary is not None
    assert resp.data.summary.total == 3
    assert resp.data.summary.skipped == 1
    assert resp.data.summary.duration_seconds == 0.75


@pytest.mark.asyncio
async def test_terminal_test_job_synthesizes_non_null_summary(monkeypatch):
    from services.tools.run_tests import get_test_job

    async def fake_send_with_unity_instance(*args, **kwargs):
        return {
            "success": True,
            "data": {
                "job_id": "job-1",
                "status": "failed",
                "started_unix_ms": 1_000,
                "finished_unix_ms": 3_500,
                "progress": {
                    "completed": 2,
                    "total": 4,
                    "failures_so_far": [{"full_name": "Tests.Bad", "message": "boom"}],
                },
                "result": None,
            },
        }

    import services.tools.run_tests as mod
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)

    resp = await get_test_job(DummyContext(), job_id="job-1")

    assert resp.data.summary is not None
    assert resp.data.summary.model_dump() == {
        "total": 4,
        "passed": 1,
        "failed": 1,
        "skipped": 0,
        "duration_seconds": 2.5,
    }


def test_testing_group_is_enabled_by_default():
    # A tool in a disabled group is absent from tools/list entirely, so an agent cannot
    # search its way to run_tests — it has to already know the manage_tools activation
    # call. These two are cheap enough to just ship visible.
    from services.registry.tool_registry import DEFAULT_ENABLED_GROUPS

    assert "testing" in DEFAULT_ENABLED_GROUPS


@pytest.mark.asyncio
async def test_run_tests_forwards_detail_flags_under_their_new_names(monkeypatch):
    from services.tools.run_tests import run_tests

    captured = {}

    async def fake_send(send_fn, unity_instance, command_type, params, **kwargs):
        captured["params"] = params
        return {"success": True, "data": {"job_id": "abc123", "status": "running", "mode": "EditMode"}}

    import services.tools.run_tests as mod
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", fake_send)

    await run_tests(DummyContext(), include_failed=True, include_skipped=True)

    assert captured["params"]["includeFailed"] is True
    assert captured["params"]["includeSkipped"] is True
    assert "includeFailedTests" not in captured["params"]


@pytest.mark.asyncio
async def test_run_tests_leaves_skipped_detail_off_by_default(monkeypatch):
    # The point of the split: a green run with a standing [Explicit] block should not re-send
    # the same skip sentences every time.
    from services.tools.run_tests import run_tests

    captured = {}

    async def fake_send(send_fn, unity_instance, command_type, params, **kwargs):
        captured["params"] = params
        return {"success": True, "data": {"job_id": "abc123", "status": "running", "mode": "EditMode"}}

    import services.tools.run_tests as mod
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", fake_send)

    await run_tests(DummyContext(), include_failed=True)

    assert "includeSkipped" not in captured["params"]


@pytest.mark.asyncio
async def test_run_tests_with_wait_timeout_returns_the_finished_job(monkeypatch):
    # One call instead of start-then-poll: the caller who is willing to sit through the run
    # gets the result back from run_tests itself.
    from services.tools.run_tests import run_tests

    sent = []

    async def fake_send(send_fn, unity_instance, command_type, params, **kwargs):
        sent.append(command_type)
        if command_type == "run_tests":
            return {"success": True, "data": {"job_id": "j1", "status": "running", "mode": "EditMode"}}
        return {
            "success": True,
            "data": {
                "job_id": "j1",
                "status": "succeeded",
                "mode": "EditMode",
                "summary": {"total": 3, "passed": 3, "failed": 0, "skipped": 0, "duration_seconds": 1.0},
            },
        }

    import services.tools.run_tests as mod
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", fake_send)

    resp = await run_tests(DummyContext(), wait_timeout=30)

    assert sent == ["run_tests", "get_test_job"]
    assert resp.data.status == "succeeded"
    assert resp.data.summary.total == 3


@pytest.mark.asyncio
async def test_run_tests_without_wait_timeout_still_hands_back_a_job_id(monkeypatch):
    from services.tools.run_tests import run_tests

    sent = []

    async def fake_send(send_fn, unity_instance, command_type, params, **kwargs):
        sent.append(command_type)
        return {"success": True, "data": {"job_id": "j1", "status": "running", "mode": "EditMode"}}

    import services.tools.run_tests as mod
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", fake_send)

    resp = await run_tests(DummyContext())

    assert sent == ["run_tests"]
    assert resp.data.job_id == "j1"


@pytest.mark.asyncio
async def test_wait_timeout_bounds_a_blocked_test_job_fetch(monkeypatch):
    from services.tools.run_tests import _wait_for_test_job

    fetch_cancelled = asyncio.Event()

    async def blocked_fetch(*args, **kwargs):
        try:
            await asyncio.sleep(60)
        finally:
            fetch_cancelled.set()

    import services.tools.run_tests as mod
    monkeypatch.setattr(mod, "_fetch_test_job", blocked_fetch)

    started = time.monotonic()
    resp = await _wait_for_test_job(None, "j1", {}, 0.05)
    elapsed = time.monotonic() - started

    assert elapsed < 0.5
    assert fetch_cancelled.is_set()
    assert resp.success is False
    assert resp.data == {"job_id": "j1"}
    assert "wait_timeout expired" in resp.error


@pytest.mark.asyncio
async def test_wait_timeout_does_not_fetch_again_after_deadline_sleep(monkeypatch):
    from services.tools.run_tests import _wait_for_test_job

    calls = 0

    async def fetch_running(*args, **kwargs):
        nonlocal calls
        calls += 1
        return {"success": True, "data": {"job_id": "j1", "status": "running"}}

    import services.tools.run_tests as mod
    monkeypatch.setattr(mod, "_fetch_test_job", fetch_running)

    resp = await _wait_for_test_job(None, "j1", {}, 0.05)

    assert calls == 1
    assert resp.success is True
    assert resp.data.status == "running"


@pytest.mark.asyncio
async def test_get_test_job_carries_skipped_reasons_without_the_skipped_tests(monkeypatch):
    from services.tools.run_tests import get_test_job

    async def fake_send(send_fn, unity_instance, command_type, params, **kwargs):
        return {
            "success": True,
            "data": {
                "job_id": "j1",
                "status": "succeeded",
                "mode": "EditMode",
                "summary": {"total": 9, "passed": 1, "failed": 0, "skipped": 8, "duration_seconds": 2.0},
                "result": {
                    "mode": "EditMode",
                    "summary": {
                        "total": 9, "passed": 1, "failed": 0, "skipped": 8,
                        "durationSeconds": 2.0, "resultState": "Passed",
                    },
                    "results": None,
                    "skipped_reasons": [{"reason": "needs a live server", "count": 8}],
                },
            },
        }

    import services.tools.run_tests as mod
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", fake_send)

    resp = await get_test_job(DummyContext(), job_id="j1")

    assert resp.data.result.results is None
    assert resp.data.result.skipped_reasons[0].count == 8


@pytest.mark.asyncio
async def test_run_tests_refuses_red_compile_with_the_error_list(monkeypatch):
    """A run that cannot start must say what broke, not just that it is blocked."""
    from services.tools.run_tests import run_tests
    import services.tools.preflight as preflight_mod
    import services.tools.run_tests as mod

    details = [
        {"file": "Assets/Scripts/Player.cs", "line": 42, "column": 9,
         "message": "error CS1002: ; expected"},
    ]

    async def fake_state(ctx):
        return {"success": True, "data": {
            "tests": {"is_running": False},
            "compilation": {"is_compiling": False, "last_compile_errors": 1,
                            "last_compile_error_details": details},
        }}

    async def unreachable_send(*args, **kwargs):
        raise AssertionError("run_tests must not reach Unity while scripts are red")

    monkeypatch.setattr(preflight_mod, "_in_pytest", lambda: False)
    monkeypatch.setattr(
        "services.resources.editor_state.get_editor_state", fake_state)
    monkeypatch.setattr(mod.unity_transport,
                        "send_with_unity_instance", unreachable_send)

    resp = await run_tests(DummyContext())

    assert resp.success is False
    assert resp.error == "compile_errors"
    assert resp.data["error_details"] == details
    assert "Assets/Scripts/Player.cs(42,9): error CS1002: ; expected" in resp.message
    assert "refresh_unity" in resp.message


@pytest.mark.asyncio
async def test_run_tests_proceeds_when_the_last_compile_was_clean(monkeypatch):
    from services.tools.run_tests import run_tests
    import services.tools.preflight as preflight_mod
    import services.tools.run_tests as mod

    async def fake_state(ctx):
        return {"success": True, "data": {
            "tests": {"is_running": False},
            "compilation": {"is_compiling": False, "last_compile_errors": 0},
        }}

    async def fake_send(send_fn, unity_instance, command_type, params, **kwargs):
        return {"success": True, "data": {"job_id": "job-1", "status": "running", "mode": "EditMode"}}

    monkeypatch.setattr(preflight_mod, "_in_pytest", lambda: False)
    monkeypatch.setattr(
        "services.resources.editor_state.get_editor_state", fake_state)
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", fake_send)

    resp = await run_tests(DummyContext())

    assert resp.success is True
    assert resp.data.job_id == "job-1"


@pytest.mark.asyncio
async def test_preflight_rechecks_state_after_its_own_refresh(monkeypatch):
    """refresh_if_dirty can itself compile, so the stale pre-refresh record must not decide."""
    import services.tools.preflight as preflight_mod

    reads = 0

    async def fake_state(ctx):
        nonlocal reads
        reads += 1
        # First read is the dirty, still-red snapshot; the refresh below fixes it.
        errors = 1 if reads == 1 else 0
        return {"success": True, "data": {
            "assets": {"external_changes_dirty": reads == 1},
            "tests": {"is_running": False},
            "compilation": {"is_compiling": False, "last_compile_errors": errors},
        }}

    async def fake_refresh(ctx, **kwargs):
        return {"success": True}

    monkeypatch.setattr(preflight_mod, "_in_pytest", lambda: False)
    monkeypatch.setattr(
        "services.resources.editor_state.get_editor_state", fake_state)
    monkeypatch.setattr("services.tools.refresh_unity.refresh_unity", fake_refresh)

    gate = await preflight_mod.preflight(
        DummyContext(), requires_no_tests=True, wait_for_no_compile=True,
        requires_clean_compile=True, refresh_if_dirty=True)

    assert gate is None
    assert reads == 2
