from __future__ import annotations

import asyncio
import logging
import os
import time
import uuid
from collections.abc import Awaitable, Callable
from dataclasses import dataclass
from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from models import MCPResponse
from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
import transport.unity_transport as unity_transport
import transport.legacy.unity_connection as _legacy_conn
from transport.legacy.unity_connection import _extract_response_reason
from services.state.external_changes_scanner import external_changes_scanner
import services.resources.editor_state as editor_state

logger = logging.getLogger(__name__)

# Blocking reasons that indicate Unity is actually busy (not just stale status).
# Must match activityPhase values from EditorStateCache.cs
_REAL_BLOCKING_REASONS = {
    "compiling", "domain_reload", "running_tests", "asset_import",
    "asset_refresh", "playmode_transition",
}
_REFRESH_JOBS: dict[str, dict[str, Any]] = {}
_REFRESH_JOB_TTL_SECONDS = 10 * 60
_MAX_REFRESH_JOBS = 256


def _prune_refresh_jobs(now: float | None = None) -> None:
    """Remove expired jobs and cap the registry to its newest entries."""
    current_time = time.monotonic() if now is None else now
    stale_job_ids = [
        job_id
        for job_id, job in _REFRESH_JOBS.items()
        if not isinstance(job.get("created_at"), (int, float))
        or current_time - job["created_at"] >= _REFRESH_JOB_TTL_SECONDS
    ]
    for stale_job_id in stale_job_ids:
        _REFRESH_JOBS.pop(stale_job_id, None)

    overflow = len(_REFRESH_JOBS) - _MAX_REFRESH_JOBS
    if overflow > 0:
        oldest_jobs = sorted(
            _REFRESH_JOBS,
            key=lambda job_id: _REFRESH_JOBS[job_id]["created_at"],
        )
        for oldest_job_id in oldest_jobs[:overflow]:
            _REFRESH_JOBS.pop(oldest_job_id, None)


def _register_refresh_job(job_id: str, job: dict[str, Any]) -> None:
    _prune_refresh_jobs()
    job["created_at"] = time.monotonic()
    _REFRESH_JOBS[job_id] = job
    _prune_refresh_jobs()


def _in_pytest() -> bool:
    return "PYTEST_CURRENT_TEST" in os.environ


@dataclass
class EditorReadyResult:
    ready: bool
    elapsed_seconds: float
    last_state: dict[str, Any] | None
    observed_busy: bool
    observed_compile: bool

    def __iter__(self):
        yield self.ready
        yield self.elapsed_seconds


class _UnityInstanceBoundContext:
    """Context proxy that keeps a resumable job routed to its originating editor."""

    def __init__(self, ctx: Context, unity_instance: str | None):
        self._ctx = ctx
        self._unity_instance = unity_instance

    def __getattr__(self, name: str) -> Any:
        return getattr(self._ctx, name)

    async def get_state(self, key: str, default: Any = None) -> Any:
        if key == "unity_instance":
            return self._unity_instance
        get_state = getattr(self._ctx, "get_state")
        try:
            return await get_state(key, default)
        except TypeError:
            return await get_state(key)


def _response_data(response: Any) -> dict[str, Any] | None:
    value = response.model_dump() if hasattr(response, "model_dump") else response
    if not isinstance(value, dict):
        return None
    data = value.get("data")
    return data if isinstance(data, dict) else None


async def _read_editor_state(ctx: Context) -> dict[str, Any] | None:
    try:
        return _response_data(await editor_state.get_editor_state(ctx))
    except Exception:
        return None


async def wait_for_editor_ready(
    ctx: Context,
    timeout_s: float = 30.0,
    *,
    baseline_compile_started_ms: int | None = None,
    require_compile_observation: bool = False,
    baseline_domain_reload_after_ms: int | None = None,
    require_reload_observation: bool = False,
) -> EditorReadyResult:
    """Poll editor_state until Unity is ready for tool calls.

    Returns (ready, elapsed_seconds).  Treats exceptions from
    get_editor_state as "not ready yet" so the loop survives transient
    connection errors during domain reload.

    When require_reload_observation is set, readiness additionally requires
    the domain reload that follows a successful compile to have completed.
    For an observed compile, the reload completion timestamp must be newer
    than that compile's start timestamp; an older reload that completed while
    the command was waiting in transport preflight cannot satisfy the gate.
    Between compile-finish and reload-start Unity briefly reports an idle
    state, so without this a compile=request refresh can be declared ready
    moments before the editor tears down every connection for the reload. A
    compile that finishes with errors never reloads, so it satisfies the gate.
    """
    start = time.monotonic()
    ready_ticks: set[Any] = set()
    last_state: dict[str, Any] | None = None
    observed_busy = False
    observed_compile = False
    observed_reload = False
    requested_compile_started_ms: int | None = None
    while time.monotonic() - start < timeout_s:
        try:
            data = await _read_editor_state(ctx)
            advice = (data or {}).get("advice") if isinstance(data, dict) else None
            if isinstance(advice, dict):
                last_state = data
                blocking = set(advice.get("blocking_reasons") or [])
                compilation = data.get("compilation") if isinstance(data.get("compilation"), dict) else {}
                compile_started = compilation.get("last_compile_started_unix_ms")
                if compilation.get("is_compiling") is True or compilation.get("is_domain_reload_pending") is True:
                    observed_compile = True
                if isinstance(compile_started, int) and (
                    baseline_compile_started_ms is None or compile_started > baseline_compile_started_ms
                ):
                    observed_compile = True
                    if (requested_compile_started_ms is None
                            or compile_started > requested_compile_started_ms):
                        requested_compile_started_ms = compile_started
                        # A reload observed before this compile began belongs to
                        # earlier work and cannot satisfy this compile's gate.
                        observed_reload = False
                reload_after = compilation.get("last_domain_reload_after_unix_ms")
                if require_compile_observation:
                    reload_is_new = (
                        isinstance(reload_after, int)
                        and requested_compile_started_ms is not None
                        and reload_after > requested_compile_started_ms
                    )
                else:
                    reload_is_new = (
                        isinstance(reload_after, int)
                        and (baseline_domain_reload_after_ms is None
                             or reload_after > baseline_domain_reload_after_ms)
                    )
                if reload_is_new:
                    observed_reload = True
                if blocking & _REAL_BLOCKING_REASONS:
                    observed_busy = True
                    ready_ticks.clear()
                else:
                    compile_satisfied = not require_compile_observation or observed_compile
                    compile_failed = (
                        observed_compile
                        and (not require_compile_observation
                             or requested_compile_started_ms is not None)
                        and compilation.get("is_compiling") is not True
                        and int(compilation.get("last_compile_errors") or 0) > 0
                    )
                    reload_satisfied = (
                        not require_reload_observation or observed_reload or compile_failed
                    )
                    if compile_satisfied and reload_satisfied:
                        tick = data.get("update_tick", data.get("sequence"))
                        if tick is None:
                            tick = ("poll", len(ready_ticks))
                        ready_ticks.add(tick)
                        if len(ready_ticks) >= 2:
                            return EditorReadyResult(
                                True, time.monotonic() - start, data, observed_busy, observed_compile
                            )
        except Exception:
            pass  # not ready yet — keep polling
        await asyncio.sleep(0.25)

    return EditorReadyResult(False, time.monotonic() - start, last_state, observed_busy, observed_compile)


def _compile_summary(state: dict[str, Any] | None, elapsed: float, compiled: bool) -> dict[str, Any]:
    compilation = (state or {}).get("compilation")
    compilation = compilation if isinstance(compilation, dict) else {}
    duration = compilation.get("last_compile_duration_seconds")
    return {
        "compiled": compiled,
        "errors": int(compilation.get("last_compile_errors") or 0) if compiled else 0,
        "warnings": int(compilation.get("last_compile_warnings") or 0) if compiled else 0,
        "duration_seconds": round(float(duration if duration is not None else elapsed), 3),
    }


def is_reloading_rejection(resp: Any) -> bool:
    """True when Unity rejected a command because it thinks it is reloading.

    The command was never executed, so retrying is safe.
    """
    if not isinstance(resp, dict) or resp.get("success"):
        return False
    data = resp.get("data") or {}
    return data.get("reason") == "reloading" and resp.get("hint") == "retry"


def is_connection_lost_after_send(resp: Any) -> bool:
    """True when a mutation's response indicates TCP was lost after command was sent.

    Script mutations trigger domain reload which kills the TCP connection.
    The mutation was likely executed but the response was lost.
    """
    if isinstance(resp, dict):
        if resp.get("success"):
            return False
        err = (resp.get("error") or resp.get("message") or "").lower()
    else:
        if getattr(resp, "success", None):
            return False
        err = (getattr(resp, "error", "") or "").lower()
    return "connection closed" in err or "disconnected" in err or "aborted" in err


async def send_mutation(
    ctx: Context,
    unity_instance: str | None,
    command: str,
    params: dict[str, Any],
    *,
    verify_after_disconnect: Callable[[], Awaitable[dict | None]] | None = None,
) -> dict | Any:
    """Send a non-idempotent mutation with reload recovery.

    Handles the full retry/recovery pattern for script mutations:
    1. Send with retry_on_reload=False (don't re-send if Unity is reloading)
    2. If reloading rejection (command never executed) → wait + retry once
    3. If connection lost after send → wait + verify via callback
    4. Wait for editor readiness before returning

    Args:
        verify_after_disconnect: async callable returning a replacement response
            dict if the mutation was verified after connection loss, or None to
            keep the original error response.
    """
    resp = await unity_transport.send_with_unity_instance(
        _legacy_conn.async_send_command_with_retry,
        unity_instance,
        command,
        params,
        retry_on_reload=False,
    )
    if is_reloading_rejection(resp):
        if not _in_pytest():
            await wait_for_editor_ready(ctx)
        resp = await unity_transport.send_with_unity_instance(
            _legacy_conn.async_send_command_with_retry,
            unity_instance,
            command,
            params,
            retry_on_reload=False,
        )
    if is_connection_lost_after_send(resp) and verify_after_disconnect:
        if not _in_pytest():
            await wait_for_editor_ready(ctx)
        verified = await verify_after_disconnect()
        if verified is not None:
            resp = verified
    if not _in_pytest():
        await wait_for_editor_ready(ctx)
    return resp


async def verify_edit_by_sha(
    unity_instance: str | None,
    name: str,
    path: str,
    pre_sha: str | None,
) -> bool:
    """Verify a script edit was applied by comparing SHA before and after.

    Returns True if the file's SHA changed (edit likely applied).
    """
    if not pre_sha:
        return False
    try:
        verify = await unity_transport.send_with_unity_instance(
            _legacy_conn.async_send_command_with_retry,
            unity_instance,
            "manage_script",
            {"action": "get_sha", "name": name, "path": path},
        )
        if isinstance(verify, dict) and verify.get("success"):
            new_sha = (verify.get("data") or {}).get("sha256")
            return bool(new_sha and new_sha != pre_sha)
    except Exception as exc:
        logger.debug(
            "Failed to verify edit after disconnect for %s at %s: %r",
            name, path, exc,
        )
    return False


@mcp_for_unity_tool(
    description="Refresh Unity's asset database and optionally request script compilation. This mutates transient Editor state and may trigger a domain reload. mode, scope, and compile select the work; wait_for_ready can block for readiness, and job_id resumes a timed-out refresh job.",
    annotations=ToolAnnotations(
        title="Refresh Unity",
        destructiveHint=True,
    ),
)
async def refresh_unity(
    ctx: Context,
    mode: Annotated[Literal["if_dirty", "force"], "Refresh mode"] = "if_dirty",
    scope: Annotated[Literal["assets", "scripts", "all"],
                     "Refresh scope"] = "all",
    compile: Annotated[Literal["none", "request"],
                       "Whether to request compilation"] = "none",
    wait_for_ready: Annotated[bool,
                              "If true, wait until editor_state.advice.ready_for_tools is true"] = True,
    job_id: Annotated[str | None,
                      "Resume a previously timed-out refresh job without requesting another refresh"] = None,
) -> MCPResponse | dict[str, Any]:
    unity_instance = await get_unity_instance_from_context(ctx)

    if job_id is not None:
        _prune_refresh_jobs()
        job = _REFRESH_JOBS.get(job_id)
        if job is None:
            return MCPResponse(success=False, error="REFRESH_JOB_NOT_FOUND", message="Refresh job was not found.")
        job_ctx = _UnityInstanceBoundContext(ctx, job.get("unity_instance"))
        result = await wait_for_editor_ready(
            job_ctx,
            timeout_s=60.0,
            baseline_compile_started_ms=job.get("baseline_compile_started_ms"),
            require_compile_observation=bool(job.get("compile_requested")),
            baseline_domain_reload_after_ms=job.get("baseline_domain_reload_after_ms"),
            require_reload_observation=bool(job.get("compile_requested")),
        )
        if not result.ready:
            return MCPResponse(
                success=False,
                error="EDITOR_NOT_READY",
                message="Timed out waiting for Unity editor readiness.",
                data={"job_id": job_id, "status": "timed_out", "last_observed_state": result.last_state,
                      "summary": _compile_summary(result.last_state, result.elapsed_seconds, result.observed_compile)},
            )
        _REFRESH_JOBS.pop(job_id, None)
        summary = _compile_summary(result.last_state, result.elapsed_seconds, result.observed_compile)
        if summary["errors"] > 0:
            return MCPResponse(
                success=False, error="COMPILE_FAILED", message="Unity compilation completed with errors.",
                data={"job_id": job_id, "status": "failed", "resulting_state": "idle", "summary": summary},
            )
        return MCPResponse(
            success=True,
            message="Unity refresh completed; editor is ready.",
            data={"job_id": job_id, "status": "succeeded", "resulting_state": "idle",
                  "summary": summary},
        )

    baseline_state = await _read_editor_state(ctx) if wait_for_ready else None
    baseline_compilation = (baseline_state or {}).get("compilation")
    baseline_compilation = baseline_compilation if isinstance(baseline_compilation, dict) else {}
    baseline_compile_started_ms = baseline_compilation.get("last_compile_started_unix_ms")
    if compile == "request" and not isinstance(baseline_compile_started_ms, int):
        # Prevent an old terminal compile record from satisfying a request when
        # the baseline snapshot was incomplete.
        baseline_compile_started_ms = int(time.time() * 1000) - 1000
    baseline_domain_reload_after_ms = baseline_compilation.get("last_domain_reload_after_unix_ms")
    if compile == "request" and not isinstance(baseline_domain_reload_after_ms, int):
        baseline_domain_reload_after_ms = int(time.time() * 1000) - 1000
    refresh_job_id = str(uuid.uuid4())
    _register_refresh_job(refresh_job_id, {
        "unity_instance": unity_instance,
        "baseline_compile_started_ms": baseline_compile_started_ms,
        "baseline_domain_reload_after_ms": baseline_domain_reload_after_ms,
        "compile_requested": compile == "request",
    })

    params: dict[str, Any] = {
        "mode": mode,
        "scope": scope,
        "compile": compile,
        "wait_for_ready": False,
        "job_id": refresh_job_id,
    }

    recovered_from_disconnect = False
    # Don't retry on reload - refresh_unity triggers compilation/reload,
    # so retrying would cause multiple reloads (issue #577)
    response = await unity_transport.send_with_unity_instance(
        _legacy_conn.async_send_command_with_retry,
        unity_instance,
        "refresh_unity",
        params,
        retry_on_reload=False,
    )

    # Handle connection errors during refresh/compile gracefully.
    # Unity disconnects during domain reload, which is expected behavior - not a failure.
    # If we sent the command and connection closed, the refresh was likely triggered successfully.
    # Convert MCPResponse to dict if needed
    response_dict = response if isinstance(response, dict) else (response.model_dump() if hasattr(response, "model_dump") else response.__dict__)
    if not response_dict.get("success", True):
        hint = response_dict.get("hint")
        err = (response_dict.get("error") or response_dict.get("message") or "").lower()
        reason = _extract_response_reason(response_dict)
        response_data = response_dict.get("data")
        response_stage = response_data.get("stage") if isinstance(response_data, dict) else None

        if response_stage == "preflight":
            # The transport exhausted its reload wait before dispatching the command.
            # A polling-only job cannot resume work that Unity never received.
            _REFRESH_JOBS.pop(refresh_job_id, None)
            return MCPResponse(**response_dict)

        # Connection closed/timeout during compile = refresh was triggered, Unity is reloading
        # This is SUCCESS, not failure - don't return error to prevent Claude Code from retrying
        is_connection_lost = (
            "connection closed" in err
            or "disconnected" in err
            or "aborted" in err  # WinError 10053: connection aborted
            or "timeout" in err
            or (reason == "reloading" and response_stage != "preflight")
        )

        if is_connection_lost and compile == "request":
            # EXPECTED BEHAVIOR: When compile="request", Unity triggers domain reload which
            # causes connection to close mid-command. This is NOT a failure - the refresh
            # was successfully triggered. Treating this as success prevents Claude Code from
            # retrying unnecessarily (which would cause multiple domain reloads - issue #577).
            # The subsequent wait_for_ready loop (below) will verify Unity becomes ready.
            logger.info("refresh_unity: Connection lost during compile (expected - domain reload triggered)")
            recovered_from_disconnect = True
        elif hint == "retry" or "could not connect" in err:
            # A retryable failure after dispatch can proceed to the readiness loop.
            if not wait_for_ready:
                _REFRESH_JOBS.pop(refresh_job_id, None)
                return MCPResponse(**response_dict)
            recovered_from_disconnect = True
        else:
            # Non-recoverable error - connection issue unrelated to domain reload
            logger.warning(f"refresh_unity: Non-recoverable error (compile={compile}): {err[:100]}")
            _REFRESH_JOBS.pop(refresh_job_id, None)
            return MCPResponse(**response_dict)

    # Optional server-side wait loop (defensive): if Unity tool doesn't wait or returns quickly,
    # poll the canonical editor_state resource until ready or timeout.
    ready_confirmed = False
    ready_result: EditorReadyResult | None = None
    if wait_for_ready:
        ready_result = await wait_for_editor_ready(
            ctx,
            timeout_s=60.0,
            baseline_compile_started_ms=baseline_compile_started_ms,
            require_compile_observation=compile == "request",
            baseline_domain_reload_after_ms=baseline_domain_reload_after_ms,
            require_reload_observation=compile == "request",
        )
        ready_confirmed = ready_result.ready

        # If we timed out without confirming readiness, log and return failure
        if not ready_confirmed:
            logger.warning("refresh_unity: Timed out after 60s waiting for editor to become ready")
            return MCPResponse(
                success=False,
                error="EDITOR_NOT_READY",
                message="Refresh triggered but timed out after 60s waiting for editor readiness.",
                data={"job_id": refresh_job_id, "status": "timed_out",
                      "last_observed_state": ready_result.last_state,
                      "summary": _compile_summary(ready_result.last_state, ready_result.elapsed_seconds,
                                                  ready_result.observed_compile)},
            )

    # After readiness is restored, clear any external-dirty flag for this instance so future tools can proceed cleanly.
    try:
        inst = unity_instance or await editor_state.infer_single_instance_id(ctx)
        if inst:
            external_changes_scanner.clear_dirty(inst)
    except Exception:
        pass

    if not wait_for_ready and compile == "request":
        # Keep every acknowledged no-wait compile resumable. Unity does not echo
        # the server-generated job ID, so returning the transport response would
        # otherwise discard the only handle callers can use to obtain the summary.
        return MCPResponse(
            success=True,
            message="Refresh requested; Unity compilation is running.",
            data={"job_id": refresh_job_id, "status": "running", "resulting_state": "compiling",
                  "recovered_from_disconnect": recovered_from_disconnect},
        )

    if wait_for_ready and ready_result is not None:
        _REFRESH_JOBS.pop(refresh_job_id, None)
        summary = _compile_summary(ready_result.last_state, ready_result.elapsed_seconds,
                                   ready_result.observed_compile)
        if summary["errors"] > 0:
            return MCPResponse(
                success=False, error="COMPILE_FAILED", message="Unity compilation completed with errors.",
                data={"job_id": refresh_job_id, "status": "failed", "resulting_state": "idle",
                      "recovered_from_disconnect": recovered_from_disconnect, "summary": summary},
            )
        return MCPResponse(
            success=True,
            message="Unity refresh completed; editor is ready.",
            data={"job_id": refresh_job_id, "status": "succeeded", "resulting_state": "idle",
                  "recovered_from_disconnect": recovered_from_disconnect,
                  "summary": summary},
        )

    _REFRESH_JOBS.pop(refresh_job_id, None)
    return MCPResponse(**response_dict) if isinstance(response, dict) else response
