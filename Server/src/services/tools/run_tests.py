"""Async Unity Test Runner jobs: start + poll."""
from __future__ import annotations

import asyncio
import logging
import time
from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations
from pydantic import BaseModel

from models import MCPResponse
from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from services.tools.preflight import preflight
from services.tools.job_contract import ensure_test_job_summary
import transport.unity_transport as unity_transport
from transport.legacy.unity_connection import async_send_command_with_retry
from transport.plugin_hub import PluginHub
from utils.focus_nudge import nudge_unity_focus, should_nudge, reset_nudge_backoff

logger = logging.getLogger(__name__)

# Strong references to background fire-and-forget tasks to prevent premature GC.
_background_tasks: set[asyncio.Task] = set()


async def _get_unity_project_path(unity_instance: str | None) -> str | None:
    """Get the project root path for a Unity instance (for focus nudging).

    Args:
        unity_instance: Unity instance hash or "Name@hash" format or None

    Returns:
        Project root path (e.g., "/Users/name/project"), or falls back to project_name if path unavailable
    """
    if not unity_instance:
        return None

    try:
        registry = PluginHub._registry
        if not registry:
            return None

        # Parse Name@hash format if present (middleware stores instances as "Name@hash")
        target_hash = unity_instance
        if "@" in target_hash:
            _, _, target_hash = target_hash.rpartition("@")
        if not target_hash:
            return None

        # Get session by hash
        session_id = await registry.get_session_id_by_hash(target_hash)
        if not session_id:
            return None

        session = await registry.get_session(session_id)
        if not session:
            return None

    except Exception as e:
        # Re-raise cancellation errors so task cancellation propagates
        if isinstance(e, asyncio.CancelledError):
            raise
        logger.debug(f"Could not get Unity project path: {e}")
        return None
    else:
        # Return full path if available, otherwise fall back to project name
        if session.project_path:
            return session.project_path
        return session.project_name if session.project_name else None


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

    if not isinstance(response, dict):
        return MCPResponse(success=False, error=str(response))
    if not response.get("success", True):
        return MCPResponse(**response)

    started = RunTestsStartResponse(**response)
    if not wait_timeout or wait_timeout <= 0 or started.data is None:
        return started

    return await _wait_for_test_job(
        unity_instance,
        started.data.job_id,
        _detail_params(include_details, include_failed, include_skipped),
        wait_timeout,
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
) -> GetTestJobResponse | MCPResponse:
    """Poll Unity until the job reaches a terminal state, the deadline passes, or it errors.

    Shared by run_tests and get_test_job so a caller who is willing to sit through the run
    pays for one round trip rather than a start plus a poll loop.
    """
    params: dict[str, Any] = {"job_id": job_id, **detail_params}
    loop = asyncio.get_running_loop()
    deadline = loop.time() + wait_timeout
    poll_interval = 2.0  # Poll Unity every 2 seconds
    prev_last_update_unix_ms = None
    last_response: dict[str, Any] | None = None

    # Get project path once for focus nudging (multi-instance support)
    project_path = await _get_unity_project_path(unity_instance)

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
        if status in ("succeeded", "failed", "cancelled"):
            return GetTestJobResponse(**response)

        # Detect progress and reset exponential backoff
        last_update_unix_ms = data.get("last_update_unix_ms")
        if prev_last_update_unix_ms is not None and last_update_unix_ms != prev_last_update_unix_ms:
            # Progress detected - reset exponential backoff for next potential stall
            reset_nudge_backoff()
            logger.debug(f"Test job {job_id} made progress - reset nudge backoff")
        prev_last_update_unix_ms = last_update_unix_ms

        # Check if Unity needs a focus nudge to make progress
        # This handles OS-level throttling (e.g., macOS App Nap) that can
        # stall PlayMode tests when Unity is in the background.
        # Uses exponential backoff: 1s, 2s, 4s, 8s, 10s max between nudges.
        progress = data.get("progress") or {}
        editor_is_focused = progress.get("editor_is_focused", True)
        current_time_ms = int(time.time() * 1000)

        if should_nudge(
            status=status,
            editor_is_focused=editor_is_focused,
            last_update_unix_ms=last_update_unix_ms,
            current_time_ms=current_time_ms,
            # Use default stall_threshold_ms (3s)
        ):
            logger.info(f"Test job {job_id} appears stalled (unfocused Unity), attempting nudge...")
            # Lazily resolve project path if not yet available (registry may have become ready)
            if project_path is None:
                project_path = await _get_unity_project_path(unity_instance)
            # Pass project path for multi-instance support
            nudged = await nudge_unity_focus(unity_project_path=project_path)
            if nudged:
                logger.info(f"Test job {job_id} nudge completed")

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
    detail_params = _detail_params(include_details, include_failed, include_skipped)

    if wait_timeout and wait_timeout > 0:
        return await _wait_for_test_job(unity_instance, job_id, detail_params, wait_timeout)

    response = await _fetch_test_job(unity_instance, {"job_id": job_id, **detail_params})
    if not isinstance(response, dict):
        return MCPResponse(success=False, error=str(response))
    if not response.get("success", True):
        return MCPResponse(**response)

    # Fire-and-forget nudge check: even without wait_timeout, clients may poll
    # externally. Check if Unity needs a nudge on every call so stalls get
    # detected regardless of polling style.
    data = response.get("data", {})
    status = data.get("status", "")
    if status == "running":
        progress = data.get("progress") or {}
        editor_is_focused = progress.get("editor_is_focused", True)
        last_update_unix_ms = data.get("last_update_unix_ms")
        current_time_ms = int(time.time() * 1000)
        if should_nudge(
            status=status,
            editor_is_focused=editor_is_focused,
            last_update_unix_ms=last_update_unix_ms,
            current_time_ms=current_time_ms,
        ):
            logger.info(f"Test job {job_id} appears stalled (unfocused Unity), scheduling background nudge...")
            project_path = await _get_unity_project_path(unity_instance)
            task = asyncio.create_task(nudge_unity_focus(unity_project_path=project_path))
            _background_tasks.add(task)
            task.add_done_callback(_background_tasks.discard)

    return GetTestJobResponse(**response)
