"""Bounded, opt-in Unity lifecycle and serialized-property tracing."""

from typing import Annotated, Any, Literal

from fastmcp import Context
from pydantic import Field

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.legacy.unity_connection import async_send_command_with_retry
from transport.unity_transport import send_with_unity_instance


@mcp_for_unity_tool(
    group="core",
    description=(
        "Start, poll, inspect, or stop a bounded opt-in trace of lifecycle callbacks, "
        "selection changes, and whitelisted serialized-property changes. Temporary "
        "instrumentation is removed on stop, timeout, play-mode exit, or domain reload."
    ),
)
async def lifecycle_trace(
    ctx: Context,
    action: Annotated[
        Literal["start", "poll", "status", "stop"],
        Field(description="Trace session operation."),
    ],
    session_id: Annotated[
        str | None,
        Field(default=None, description="Session ID returned by start; required for poll/status/stop."),
    ] = None,
    targets: Annotated[
        list[str] | str | None,
        Field(default=None, description="Hierarchy names, paths, or GlobalObjectIds to instrument."),
    ] = None,
    events: Annotated[
        list[Literal[
            "Awake", "OnEnable", "OnDisable", "OnDestroy",
            "serialized_property_change", "selection_change",
        ]] | None,
        Field(default=None, description="Event kinds to observe. Defaults to lifecycle callbacks."),
    ] = None,
    property_whitelist: Annotated[
        list[str] | None,
        Field(default=None, description="Serialized paths as ComponentType.propertyPath."),
    ] = None,
    max_events: Annotated[
        int,
        Field(default=500, ge=1, le=5000, description="Maximum retained events (1-5000)."),
    ] = 500,
    timeout_seconds: Annotated[
        int,
        Field(default=300, ge=1, le=3600, description="Automatic session timeout in seconds (1-3600)."),
    ] = 300,
    cursor: Annotated[
        int,
        Field(default=0, ge=0, description="Zero-based event cursor for poll."),
    ] = 0,
    limit: Annotated[
        int,
        Field(default=100, ge=1, le=500, description="Maximum events returned by poll (1-500)."),
    ] = 100,
) -> dict[str, Any]:
    normalized_targets = [targets] if isinstance(targets, str) else (targets or [])
    if action == "start":
        if not normalized_targets or any(not item.strip() for item in normalized_targets):
            return {"success": False, "message": "Start requires at least one non-empty target."}
        if len(normalized_targets) > 50:
            return {"success": False, "message": "At most 50 targets may be traced."}
        if property_whitelist and len(property_whitelist) > 100:
            return {"success": False, "message": "At most 100 serialized properties may be traced."}
    elif not session_id or not session_id.strip():
        return {"success": False, "message": f"{action} requires a non-empty session_id."}

    params: dict[str, Any] = {
        "action": action,
        "maxEvents": max_events,
        "timeoutSeconds": timeout_seconds,
        "cursor": cursor,
        "limit": limit,
    }
    if session_id:
        params["sessionId"] = session_id
    if action == "start":
        params["targets"] = normalized_targets
        params["events"] = events or ["Awake", "OnEnable", "OnDisable", "OnDestroy"]
        params["propertyWhitelist"] = property_whitelist or []

    unity_instance = await get_unity_instance_from_context(ctx)
    try:
        response = await send_with_unity_instance(
            async_send_command_with_retry,
            unity_instance,
            "lifecycle_trace",
            params,
        )
        if isinstance(response, dict):
            return response
        return {"success": False, "message": str(response)}
    except Exception as exc:
        return {"success": False, "message": f"Error managing lifecycle trace: {exc!s}"}
