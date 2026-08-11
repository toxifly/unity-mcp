from typing import Annotated, Any, Optional

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry

SESSION_ACTIONS = [
    "profiler_start", "profiler_stop", "profiler_status", "profiler_set_areas",
]

COUNTER_ACTIONS = [
    "get_frame_timing", "get_counters", "get_object_memory",
]

MEMORY_SNAPSHOT_ACTIONS = [
    "memory_take_snapshot", "memory_list_snapshots", "memory_compare_snapshots",
]

FRAME_DEBUGGER_ACTIONS = [
    "frame_debugger_enable", "frame_debugger_disable", "frame_debugger_get_events",
]

UTILITY_ACTIONS = ["ping"]

ALL_ACTIONS = (
    UTILITY_ACTIONS + SESSION_ACTIONS + COUNTER_ACTIONS
    + MEMORY_SNAPSHOT_ACTIONS + FRAME_DEBUGGER_ACTIONS
)


@mcp_for_unity_tool(
    group="profiling",
    description=(
        "Control Unity Profiler sessions, read counters and frame timing, inspect object memory, capture or "
        "compare memory snapshots, and use the Frame Debugger. action supports profiler_start/stop/status/"
        "set_areas, get_frame_timing, get_counters, get_object_memory, memory_take/list/compare_snapshots, "
        "and frame_debugger_enable/disable/get_events. The remaining parameters are action-specific; "
        "page_size and cursor page Frame Debugger events. Memory snapshots require com.unity.memoryprofiler "
        "and write .snap files; profiler_start may write a .raw recording. Session and Frame Debugger "
        "controls mutate transient Editor state."
    ),
    annotations=ToolAnnotations(
        title="Manage Profiler",
        destructiveHint=False,
        readOnlyHint=False,
    ),
)
async def manage_profiler(
    ctx: Context,
    action: Annotated[str, "The profiler action to perform."],
    category: Annotated[Optional[str], "Profiler category name for get_counters (e.g. Render, Scripts, Memory, Physics)."] = None,
    counters: Annotated[Optional[list[str]], "Specific counter names for get_counters. Omit to read all in category."] = None,
    object_path: Annotated[Optional[str], "Scene hierarchy or asset path for get_object_memory."] = None,
    log_file: Annotated[Optional[str], "Path to .raw file for profiler_start recording."] = None,
    enable_callstacks: Annotated[Optional[bool], "Enable allocation callstacks for profiler_start."] = None,
    areas: Annotated[Optional[dict[str, bool]], "Dict of area name to bool for profiler_set_areas."] = None,
    snapshot_path: Annotated[Optional[str], "Output path for memory_take_snapshot."] = None,
    search_path: Annotated[Optional[str], "Search directory for memory_list_snapshots."] = None,
    snapshot_a: Annotated[Optional[str], "First snapshot path for memory_compare_snapshots."] = None,
    snapshot_b: Annotated[Optional[str], "Second snapshot path for memory_compare_snapshots."] = None,
    page_size: Annotated[Optional[int], "Page size for frame_debugger_get_events (default 50)."] = None,
    cursor: Annotated[Optional[int], "Cursor offset for frame_debugger_get_events."] = None,
) -> dict[str, Any]:
    action_lower = action.lower()
    if action_lower not in ALL_ACTIONS:
        return {
            "success": False,
            "message": f"Unknown action '{action}'. Valid actions: {', '.join(ALL_ACTIONS)}",
        }

    unity_instance = await get_unity_instance_from_context(ctx)

    params_dict: dict[str, Any] = {"action": action_lower}

    param_map = {
        "category": category, "counters": counters,
        "object_path": object_path,
        "log_file": log_file, "enable_callstacks": enable_callstacks,
        "areas": areas,
        "snapshot_path": snapshot_path, "search_path": search_path,
        "snapshot_a": snapshot_a, "snapshot_b": snapshot_b,
        "page_size": page_size, "cursor": cursor,
    }
    for key, val in param_map.items():
        if val is not None:
            params_dict[key] = val

    result = await send_with_unity_instance(
        async_send_command_with_retry, unity_instance, "manage_profiler", params_dict
    )
    return result if isinstance(result, dict) else {"success": False, "message": str(result)}
