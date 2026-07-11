"""Read-only operating guidance for efficient and safe Unity MCP workflows."""

from typing import Any

from fastmcp import Context

from services.registry import mcp_for_unity_resource


_SECTIONS: dict[str, dict[str, Any]] = {
    "instance_routing": {
        "instructions": [
            "Read mcpforunity://instances to list connected sessions as Name@hash.",
            "When several instances are connected, call set_active_instance with the exact Name@hash to pin session routing before reading resources or calling tools.",
            "For a one-off tool call, pass unity_instance as Name@hash, a unique hash prefix, or (in stdio mode) a port number; this does not change the session default.",
            "Set unity_instance on the outer batch_execute call. Per-command instance routing inside a batch is not supported.",
        ],
    },
    "safe_mutation": {
        "instructions": [
            "Read the related resource before mutating editor state, identify the exact object or asset scope, and use forward-slash paths relative to Assets/ unless a tool says otherwise.",
            "Prefer dry_run and change_guard on supported mutation tools to preview exact serialized changes and roll back unexpected changes.",
            "Prefer save_scene_scoped, save_prefab_scoped, and save_assets_scoped over broad saves; use preview_asset_changes when only inspection is required.",
            "Use manage_prefabs(action='create_and_replace') when prefab creation and scene replacement must be one guarded transaction.",
            "Inspect each result's success, warnings, error, and rollback or dirty-state report before continuing. Use undo/redo only as an explicit recovery action.",
            "For new scenes, include a Camera and main Directional Light. Use manage_scene for scene lifecycle and prefabs for reusable GameObjects.",
        ],
    },
    "script_compilation": {
        "instructions": [
            "After creating or editing scripts, call refresh_unity(wait_for_ready=true) before using new types or components.",
            "If refresh_unity times out, resume the same refresh with its job_id instead of requesting another refresh or manually polling editor state.",
            "For asynchronous test runs, call get_test_job with wait_timeout for a blocking wait instead of repeatedly polling job status.",
            "After Unity is ready, call read_console filtered to Error and resolve compilation errors before proceeding.",
            "When the docs group is available, verify version- and package-specific APIs with unity_reflect and unity_docs before writing C#; search project assets for actual shader names.",
        ],
    },
    "payload_sizing": {
        "instructions": [
            "Prefer summary-first, filtered, paged reads and request detailed properties only when needed.",
            "For manage_scene(action='get_hierarchy'), start with page_size=50 and follow next_cursor until it is null.",
            "For manage_gameobject(action='get_components'), keep include_properties=false for metadata-only reads. When properties are needed, use page_size=3-10 plus property_whitelist or property_blacklist.",
            "For manage_asset(action='search'), use page_size=25-50 with page_number and keep generate_preview=false unless thumbnails are required because previews may contain large base64 payloads.",
            "Use batch_execute for multiple independent operations, within the batch size reported by mcpforunity://editor/state.",
        ],
    },
    "tool_groups": {
        "instructions": [
            "Read resources by their exact URI from resources/list, not by resource name or a guessed name-to-path conversion; response content is wrapped under the top-level data field.",
            "Read mcpforunity://tool-groups to discover group membership and current defaults.",
            "Use manage_tools to list, activate, deactivate, sync, or reset tool-group visibility. The core group is enabled by default; server meta-tools remain visible.",
            "Read mcpforunity://capabilities before relying on optional or versioned workflows.",
            "When project-scoped custom tools are enabled, read mcpforunity://custom-tools. Otherwise, connected Unity custom tools are registered as standard tools.",
            "Use resources for read-only state and tools for actions. Read mcpforunity://menu-items before execute_menu_item.",
        ],
    },
}


@mcp_for_unity_resource(
    uri="mcpforunity://workflow",
    name="workflow",
    description=(
        "Read-only operating guide for Unity MCP instance routing, safe mutation, "
        "script compilation, payload sizing, and tool groups. Read before first use.\n\n"
        "URI: mcpforunity://workflow"
    ),
)
async def get_workflow(ctx: Context) -> dict[str, Any]:
    """Return stable workflow sections without contacting or mutating Unity."""
    del ctx
    return {
        "schema_version": 1,
        "sections": _SECTIONS,
    }
