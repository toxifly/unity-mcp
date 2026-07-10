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
