"""Async Unity Test Runner jobs: start + poll."""
from __future__ import annotations

import asyncio
from collections import OrderedDict
from dataclasses import dataclass
from itertools import count
import logging
import ntpath
import posixpath
import time
from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations
from pydantic import BaseModel

from models import MCPResponse
from core.config import config
from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from services.tools.preflight import preflight
from services.tools.job_contract import ensure_test_job_summary
import transport.unity_transport as unity_transport
from transport.legacy.unity_connection import _extract_response_reason, async_send_command_with_retry
import services.tools.refresh_unity as refresh_tool
from transport.plugin_hub import PluginHub
from utils import focus_nudge
from utils.focus_nudge import nudge_unity_focus, should_nudge

logger = logging.getLogger(__name__)

# Strong references to background fire-and-forget tasks to prevent premature GC.
_background_tasks: set[asyncio.Task] = set()
_active_nudge_task: asyncio.Task | None = None
_MAX_JOB_NUDGES = 3
_NUDGE_STATE_TTL_S = 3600.0
_MAX_NUDGE_STATES = 256
_poll_observation_order = count()


@dataclass
class _JobNudgeState:
    last_seen: float
    last_update: int = 0
    completed: int = 0
    test_started: int = 0
    test_finished: int = 0
    attempts: int = 0
    last_attempt: float | None = None
    task: asyncio.Task | None = None
    run_in_background: bool = False
    editor_is_focused: bool = True
    latest_observation: int = -1


_nudge_states: OrderedDict[tuple[str, str, str, str], _JobNudgeState] = OrderedDict()
_terminal_nudge_jobs: OrderedDict[tuple[str, str, str, str], float] = OrderedDict()


async def _get_unity_project_path(unity_instance: str | None, user_id: str | None = None) -> str | None:
    """Get the project root path for a Unity instance (for focus nudging).

    Args:
        unity_instance: Unity instance hash or "Name@hash" format or None

    Returns:
        Exact absolute project root path, or None if the identity is unresolved.
    """
    if not unity_instance:
        return None

    try:
        # Parse Name@hash format if present (middleware stores instances as "Name@hash")
        target_hash = unity_instance
        if "@" in target_hash:
            _, _, target_hash = target_hash.rpartition("@")
        if not target_hash:
            return None

        if unity_transport._is_http_transport():
            registry = PluginHub._registry
            if not registry or (config.http_remote_hosted and not user_id):
                return None
            session_id = await registry.get_session_id_by_hash(target_hash, user_id=user_id)
            session = await registry.get_session(session_id) if session_id else None
            path = session.project_path if session else None
        else:
            from transport.legacy.stdio_port_registry import stdio_port_registry
            instances = stdio_port_registry.get_instances()
            matches = [instance for instance in instances if (
                instance.id == unity_instance or instance.hash == target_hash
            )]
            path = matches[0].path if len(matches) == 1 else None
            # Stdio status files contain Application.dataPath, ending in Assets.
            if path:
                path_module = ntpath if ntpath.splitdrive(path)[0] or "\\" in path else posixpath
                path = path_module.normpath(path)
                if path_module.basename(path).lower() == "assets":
                    path = path_module.dirname(path)
        if not path:
            return None
        path_module = ntpath if ntpath.splitdrive(path)[0] or "\\" in path else posixpath
        if path_module is ntpath and not ntpath.splitdrive(path)[0]:
            return None
        return path_module.normpath(path) if path_module.isabs(path) else None

    except Exception as e:
        # Re-raise cancellation errors so task cancellation propagates
        if isinstance(e, asyncio.CancelledError):
            raise
        logger.debug(f"Could not get Unity project path: {e}")
        return None


async def _update_job_nudge(
    unity_instance: str | None, user_id: str | None, job_id: str,
    data: dict[str, Any], *, wait: bool, observation_order: int | None = None,
) -> None:
    """Share a bounded, monotonic no-progress budget across every poll of a job."""
    global _active_nudge_task
    if not unity_instance or (config.http_remote_hosted and not user_id):
        return
    if observation_order is None:
        observation_order = next(_poll_observation_order)
    instance_hash = unity_instance.rpartition("@")[2]
    key = (config.transport_mode, user_id or "", instance_hash, job_id)
    now = time.monotonic()
    for terminal_key, observed_at in list(_terminal_nudge_jobs.items()):
        if now - observed_at > _NUDGE_STATE_TTL_S:
            del _terminal_nudge_jobs[terminal_key]
    for old_key, old_state in list(_nudge_states.items()):
        if now - old_state.last_seen > _NUDGE_STATE_TTL_S and not (
            old_state.task and not old_state.task.done()
        ):
            del _nudge_states[old_key]
    if data.get("status") in ("succeeded", "failed", "cancelled"):
        _terminal_nudge_jobs[key] = now
        _terminal_nudge_jobs.move_to_end(key)
        while len(_terminal_nudge_jobs) > _MAX_NUDGE_STATES:
            _terminal_nudge_jobs.popitem(last=False)
        state = _nudge_states.pop(key, None)
        if state and state.task and not state.task.done():
            state.task.cancel()
        return
    if data.get("status") != "running":
        return
    progress = data.setdefault("progress", {}) or {}
    data["progress"] = progress
    if key in _terminal_nudge_jobs:
        progress["focus_nudge_status"] = "terminal_already_observed"
        return
    state = _nudge_states.get(key)
    if state is None:
        # Do not evict an observed running job: that would renew its spent budget.
        if len(_nudge_states) >= _MAX_NUDGE_STATES:
            progress["focus_nudge_status"] = "tracking_limit"
            return
        state = _JobNudgeState(last_seen=now)
        _nudge_states[key] = state
    state.last_seen = now
    _nudge_states.move_to_end(key)
    advanced = False
    for attr, value in (
        ("last_update", data.get("last_update_unix_ms")),
        ("completed", progress.get("completed")),
        ("test_started", progress.get("current_test_started_unix_ms")),
        ("test_finished", progress.get("last_finished_unix_ms")),
    ):
        if isinstance(value, int) and not isinstance(value, bool) and value > getattr(state, attr):
            setattr(state, attr, value)
            advanced = True
    if advanced:
        state.attempts = 0
        state.last_attempt = None
    # Focus can change without any test progress. Order UI observations by when
    # their polls started, not by test timestamps or by reply arrival order.
    if observation_order > state.latest_observation:
        state.latest_observation = observation_order
        state.run_in_background = progress.get("run_in_background") is True
        state.editor_is_focused = progress.get("editor_is_focused", True)
    progress["focus_nudge_attempts"] = state.attempts
    progress["focus_nudge_limit"] = _MAX_JOB_NUDGES
    if state.attempts >= _MAX_JOB_NUDGES:
        progress["stuck_suspected"] = True
        progress["focus_nudge_status"] = "attempt_limit_reached"
        return
    if state.run_in_background:
        progress["focus_nudge_status"] = "background_execution_enabled"
        return
    if not should_nudge(
        status="running", editor_is_focused=state.editor_is_focused,
        last_update_unix_ms=state.last_update or None,
        current_time_ms=int(time.time() * 1000),
    ):
        return
    if _active_nudge_task is not None and not _active_nudge_task.done():
        return
    interval = min(focus_nudge._BASE_NUDGE_INTERVAL_S * (2 ** state.attempts), focus_nudge._MAX_NUDGE_INTERVAL_S)
    if state.last_attempt is not None and now - state.last_attempt < interval:
        return
    observed_progress = (state.last_update, state.completed, state.test_started, state.test_finished)
    observed_reservation = (state.attempts, state.last_attempt)
    project_path = await _get_unity_project_path(unity_instance, user_id)
    if not project_path:
        progress["focus_nudge_status"] = "project_path_unavailable"
        return
    # Resolution can await HTTP registry locks; recheck after another poll may
    # have completed the job, reported progress, or reserved the desktop.
    if _nudge_states.get(key) is not state or observed_progress != (
        state.last_update, state.completed, state.test_started, state.test_finished
    ) or observed_reservation != (state.attempts, state.last_attempt) or (
        state.run_in_background or state.editor_is_focused
    ) or (
        _active_nudge_task is not None and not _active_nudge_task.done()
    ):
        return
    state.attempts += 1
    state.last_attempt = time.monotonic()
    progress["focus_nudge_attempts"] = state.attempts
    progress["focus_nudge_status"] = "scheduled"

    async def perform_nudge() -> None:
        await nudge_unity_focus(
            unity_project_path=project_path, force=True,
            focus_duration_s=focus_nudge._DEFAULT_FOCUS_DURATION_S,
        )

    task = asyncio.create_task(perform_nudge())
    state.task = task
    _active_nudge_task = task
    _background_tasks.add(task)

    def finish(done: asyncio.Task) -> None:
        global _active_nudge_task
        _background_tasks.discard(done)
        if _active_nudge_task is done:
            _active_nudge_task = None
        if state.task is done:
            state.task = None
        if not done.cancelled() and done.exception() is not None:
            logger.warning("Test job focus nudge failed: %s", done.exception())

    task.add_done_callback(finish)
    if wait:
        # Another poll may cancel the child after observing terminal status.
        # Child cancellation must not cancel this request; caller cancellation
        # still propagates through gather and cancels the nudge for focus restore.
        await asyncio.gather(task, return_exceptions=True)


class RunTestsSummary(BaseModel):
    total: int
    passed: int
    failed: int
    skipped: int
    durationSeconds: float
    resultState: str


class TestJobSummary(BaseModel):
    total: int
    passed: int
    failed: int
    skipped: int
    duration_seconds: float


class RunTestsTestResult(BaseModel):
    name: str
    fullName: str
    state: str
    durationSeconds: float
    message: str | None = None
    stackTrace: str | None = None
    output: str | None = None


class SkippedReason(BaseModel):
    reason: str
    count: int


class RunTestsResult(BaseModel):
    mode: str
    summary: RunTestsSummary
    results: list[RunTestsTestResult] | None = None
    skipped_reasons: list[SkippedReason] | None = None


class RunTestsStartData(BaseModel):
    job_id: str
    status: str
    mode: str | None = None
    include_details: bool | None = None
    include_failed: bool | None = None
    include_skipped: bool | None = None


class RunTestsStartResponse(MCPResponse):
    data: RunTestsStartData | None = None


def _detail_params(include_details: bool, include_failed: bool, include_skipped: bool) -> dict[str, Any]:
    params: dict[str, Any] = {}
    if include_details:
        params["includeDetails"] = True
    if include_failed:
        params["includeFailed"] = True
    if include_skipped:
        params["includeSkipped"] = True
    return params


class TestJobFailure(BaseModel):
    full_name: str | None = None
    message: str | None = None


class TestJobProgress(BaseModel):
    completed: int | None = None
    total: int | None = None
    current_test_full_name: str | None = None
    current_test_started_unix_ms: int | None = None
    last_finished_test_full_name: str | None = None
    last_finished_unix_ms: int | None = None
    stuck_suspected: bool | None = None
    editor_is_focused: bool | None = None
    run_in_background: bool | None = None
    focus_nudge_attempts: int | None = None
    focus_nudge_limit: int | None = None
    focus_nudge_status: str | None = None
    blocked_reason: str | None = None
    failures_so_far: list[TestJobFailure] | None = None
    failures_capped: bool | None = None


class GetTestJobData(BaseModel):
    job_id: str
    status: str
    mode: str | None = None
    started_unix_ms: int | None = None
    finished_unix_ms: int | None = None
    last_update_unix_ms: int | None = None
    progress: TestJobProgress | None = None
    summary: TestJobSummary | None = None
    error: str | None = None
    result: RunTestsResult | None = None


class GetTestJobResponse(MCPResponse):
    data: GetTestJobData | None = None


@mcp_for_unity_tool(
    group="testing",
    description="Start an asynchronous Unity Test Framework run and return a job_id immediately. This mutates transient test and Editor state; mode, test_names, group_names, category_names, and assembly_names filter the run.",
    annotations=ToolAnnotations(
        title="Run Tests",
        destructiveHint=True,
    ),
)
async def run_tests(
    ctx: Context,
    mode: Annotated[Literal["EditMode", "PlayMode"],
                    "Unity test mode to run"] = "EditMode",
    test_names: Annotated[list[str] | str,
                          "Full names of specific tests to run"] | None = None,
    group_names: Annotated[list[str] | str,
                           "Same as test_names, except it allows for Regex"] | None = None,
    category_names: Annotated[list[str] | str,
                              "NUnit category names to filter by"] | None = None,
    assembly_names: Annotated[list[str] | str,
                              "Assembly names to filter tests by"] | None = None,
    include_failed: Annotated[bool,
                              "Include details for failed tests (default: false)"] = False,
    include_skipped: Annotated[bool,
                               "Include details for skipped tests. Off by default: a suite with a "
                               "standing [Explicit] block re-sends the same sentences on every green "
                               "run, and result.skipped_reasons already carries them as counts."] = False,
    include_details: Annotated[bool,
                               "Include details for all tests (default: false)"] = False,
    wait_timeout: Annotated[int | None,
                            "If set, wait up to this many seconds for the run to finish and return "
                            "its result, instead of returning a job_id to poll. Saves the whole "
                            "start-then-poll round trip on runs short enough to sit through."] = None,
    init_timeout: Annotated[int | None,
                            "Initialization timeout in milliseconds. PlayMode tests may need longer "
                            "due to domain reload (default: 15000). Recommended: 120000 for PlayMode."] = None,
    clear_stuck: Annotated[bool,
                           "Recovery escape hatch: force-clear a wedged test job instead of starting a "
                           "run. Use when get_test_job stays 'running' forever or run_tests keeps "
                           "returning 'tests_running' after a runner crash. Bypasses preflight."] = False,
) -> RunTestsStartResponse | GetTestJobResponse | MCPResponse:
    unity_instance = await get_unity_instance_from_context(ctx)

    # Recovery path: clear a stuck/orphaned job without starting a new run. This must bypass
    # preflight (whose whole point is being unavailable during a wedge) and go straight to Unity.
    if clear_stuck:
        response = await unity_transport.send_with_unity_instance(
            async_send_command_with_retry,
            unity_instance,
            "run_tests",
            {"clear_stuck": True},
        )
        if isinstance(response, dict):
            return MCPResponse(**response)
        return MCPResponse(success=False, error=str(response))

    if init_timeout is not None and init_timeout <= 0:
        return MCPResponse(success=False, error="init_timeout must be a positive integer (milliseconds) or None")

    gate = await preflight(ctx, requires_no_tests=True, wait_for_no_compile=True,
                           requires_clean_compile=True, refresh_if_dirty=True)
    if isinstance(gate, MCPResponse):
        return gate

    def _coerce_string_list(value) -> list[str] | None:
        if value is None:
            return None
        if isinstance(value, str):
            return [value] if value.strip() else None
        if isinstance(value, list):
            result = [str(v).strip() for v in value if v and str(v).strip()]
            return result if result else None
        return None

    params: dict[str, Any] = {"mode": mode}
    if (t := _coerce_string_list(test_names)):
        params["testNames"] = t
    if (g := _coerce_string_list(group_names)):
        params["groupNames"] = g
    if (c := _coerce_string_list(category_names)):
        params["categoryNames"] = c
    if (a := _coerce_string_list(assembly_names)):
        params["assemblyNames"] = a
    params.update(_detail_params(include_details, include_failed, include_skipped))
    if init_timeout is not None and init_timeout > 0:
        params["initTimeout"] = init_timeout

    response = await unity_transport.send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "run_tests",
        params,
    )

    if _extract_response_reason(response) == "pending_imports":
        # Unity imported edits that were on disk but not yet imported and is recompiling. Play
        # entry would have done that import mid-transition and stalled the run; wait out the
        # reload here and start the run on the reloaded domain instead.
        response = await _rerun_after_pending_imports(ctx, unity_instance, params, response)

    if not isinstance(response, dict):
        return MCPResponse(success=False, error=str(response))
    if not response.get("success", True):
        return MCPResponse(**response)

    started = RunTestsStartResponse(**response)
    if not wait_timeout or wait_timeout <= 0 or started.data is None:
        return started

    user_id = await ctx.get_state("user_id") if config.http_remote_hosted else None
    return await _wait_for_test_job(
        unity_instance,
        started.data.job_id,
        _detail_params(include_details, include_failed, include_skipped),
        wait_timeout,
        user_id,
    )


_PENDING_IMPORTS_RELOAD_TIMEOUT_S = 120.0


async def _rerun_after_pending_imports(
    ctx: Context,
    unity_instance: str | None,
    params: dict[str, Any],
    refusal: Any,
) -> Any:
    """Wait for the reload Unity's import started, then send run_tests once more.

    The refusal arrives before that reload can finish (compiling alone takes seconds), so the
    last reload visible now is the baseline the new one must pass. A compile that fails never
    reloads; the rerun then answers compile_errors.
    """
    state = await refresh_tool._read_editor_state(ctx)
    compilation = (state or {}).get("compilation")
    baseline = compilation.get("last_domain_reload_after_unix_ms") if isinstance(compilation, dict) else None
    ready = await refresh_tool.wait_for_editor_ready(
        ctx,
        timeout_s=_PENDING_IMPORTS_RELOAD_TIMEOUT_S,
        baseline_domain_reload_after_ms=baseline if isinstance(baseline, int) else None,
        require_reload_observation=True,
    )
    if not ready.ready:
        return refusal
    return await unity_transport.send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "run_tests",
        params,
    )


async def _fetch_test_job(unity_instance: str | None, params: dict[str, Any]) -> Any:
    response = await unity_transport.send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "get_test_job",
        params,
    )
    return ensure_test_job_summary(response) if isinstance(response, dict) else response


async def _wait_for_test_job(
    unity_instance: str | None,
    job_id: str,
    detail_params: dict[str, Any],
    wait_timeout: int,
    user_id: str | None = None,
) -> GetTestJobResponse | MCPResponse:
    """Poll Unity until the job reaches a terminal state, the deadline passes, or it errors.

    Shared by run_tests and get_test_job so a caller who is willing to sit through the run
    pays for one round trip rather than a start plus a poll loop.
    """
    params: dict[str, Any] = {"job_id": job_id, **detail_params}
    loop = asyncio.get_running_loop()
    deadline = loop.time() + wait_timeout
    poll_interval = 2.0  # Poll Unity every 2 seconds
    last_response: dict[str, Any] | None = None

    while True:
        # Check before starting another transport call: the preceding sleep may have consumed the
        # entire budget. Bound the call itself as well, since transport retries can otherwise make
        # a short wait_timeout take tens of seconds.
        remaining = deadline - loop.time()
        if remaining <= 0:
            if last_response is not None:
                return GetTestJobResponse(**last_response)
            return MCPResponse(
                success=False,
                error=f"wait_timeout expired before test job '{job_id}' status could be fetched",
                data={"job_id": job_id},
            )

        observation_order = next(_poll_observation_order)
        try:
            response = await asyncio.wait_for(
                _fetch_test_job(unity_instance, params),
                timeout=remaining,
            )
        except asyncio.TimeoutError:
            if last_response is not None:
                return GetTestJobResponse(**last_response)
            return MCPResponse(
                success=False,
                error=f"wait_timeout expired while fetching test job '{job_id}' status",
                data={"job_id": job_id},
            )

        if not isinstance(response, dict):
            return MCPResponse(success=False, error=str(response))

        if not response.get("success", True):
            return MCPResponse(**response)

        last_response = response

        # Check if tests are done
        data = response.get("data", {})
        status = data.get("status", "")
        await _update_job_nudge(
            unity_instance, user_id, job_id, data, wait=True, observation_order=observation_order,
        )
        if status in ("succeeded", "failed", "cancelled"):
            return GetTestJobResponse(**response)

        # Check timeout
        remaining = deadline - loop.time()
        if remaining <= 0:
            # Timeout reached, return current status
            return GetTestJobResponse(**response)

        # Wait before next poll (but don't exceed remaining time)
        sleep_for = min(poll_interval, remaining)
        await asyncio.sleep(sleep_for)
        if sleep_for >= remaining:
            # Event-loop clocks can wake a fraction before the requested deadline on some
            # platforms. The whole remaining budget was assigned to sleeping, so return the
            # last status instead of starting one more transport request.
            return GetTestJobResponse(**response)


@mcp_for_unity_tool(
    group="testing",
    description="Read the status or results of an asynchronous Unity test job by job_id. Polling is read-only; include_failed, include_skipped and include_details control returned result detail.",
    annotations=ToolAnnotations(
        title="Get Test Job",
        readOnlyHint=True,
        destructiveHint=False,
        idempotentHint=True,
        openWorldHint=False,
    ),
)
async def get_test_job(
    ctx: Context,
    job_id: Annotated[str, "Job id returned by run_tests"],
    include_failed: Annotated[bool,
                              "Include details for failed tests (default: false)"] = False,
    include_skipped: Annotated[bool,
                               "Include details for skipped tests. Off by default: result."
                               "skipped_reasons already carries them as reason -> count."] = False,
    include_details: Annotated[bool,
                               "Include details for all tests (default: false)"] = False,
    wait_timeout: Annotated[int | None,
                            "If set, wait up to this many seconds for tests to complete before returning. "
                            "Reduces polling frequency and avoids client-side loop detection. "
                            "Recommended: 30-60 seconds. Returns immediately if tests complete sooner."] = None,
) -> GetTestJobResponse | MCPResponse:
    unity_instance = await get_unity_instance_from_context(ctx)
    user_id = await ctx.get_state("user_id") if config.http_remote_hosted else None
    detail_params = _detail_params(include_details, include_failed, include_skipped)

    if wait_timeout and wait_timeout > 0:
        return await _wait_for_test_job(unity_instance, job_id, detail_params, wait_timeout, user_id)

    observation_order = next(_poll_observation_order)
    response = await _fetch_test_job(unity_instance, {"job_id": job_id, **detail_params})
    if not isinstance(response, dict):
        return MCPResponse(success=False, error=str(response))
    if not response.get("success", True):
        return MCPResponse(**response)

    data = response.get("data", {})
    await _update_job_nudge(
        unity_instance, user_id, job_id, data, wait=False, observation_order=observation_order,
    )
    return GetTestJobResponse(**response)
