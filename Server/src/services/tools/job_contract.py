"""Compatibility helpers for the shared asynchronous job contract."""
from __future__ import annotations

from typing import Any


TERMINAL_JOB_STATUSES = frozenset({"succeeded", "failed", "cancelled", "timed_out"})


def ensure_test_job_summary(response: dict[str, Any]) -> dict[str, Any]:
    """Ensure terminal test jobs expose the canonical compact summary.

    Current Unity editors provide ``data.summary`` directly. This fallback keeps
    the server compatible with older editors that only return ``result.summary``
    or progress counters.
    """
    data = response.get("data")
    if not isinstance(data, dict) or data.get("status") not in TERMINAL_JOB_STATUSES:
        return response
    if isinstance(data.get("summary"), dict):
        return response

    result = data.get("result")
    legacy_summary = result.get("summary") if isinstance(result, dict) else None
    progress = data.get("progress") if isinstance(data.get("progress"), dict) else {}
    source = legacy_summary if isinstance(legacy_summary, dict) else {}

    failed = _integer(source.get("failed"), len(progress.get("failures_so_far") or []))
    skipped = _integer(source.get("skipped"), 0)
    total = _integer(source.get("total"), progress.get("total"), progress.get("completed"), 0)
    completed = _integer(progress.get("completed"), total)
    passed = _integer(source.get("passed"), max(0, completed - failed - skipped))
    duration = source.get("duration_seconds", source.get("durationSeconds"))
    if duration is None:
        started = data.get("started_unix_ms")
        finished = data.get("finished_unix_ms")
        duration = max(0, finished - started) / 1000 if isinstance(started, int) and isinstance(finished, int) else 0

    data["summary"] = {
        "total": total,
        "passed": passed,
        "failed": failed,
        "skipped": skipped,
        "duration_seconds": float(duration),
    }
    return response


def _integer(*values: Any) -> int:
    for value in values:
        if value is not None:
            try:
                return max(0, int(value))
            except (TypeError, ValueError):
                continue
    return 0
