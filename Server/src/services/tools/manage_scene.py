from typing import Annotated, Literal, Any

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from services.tools.utils import coerce_int, coerce_bool
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry
from services.tools.preflight import preflight


@mcp_for_unity_tool(
    description=(
        "Performs CRUD operations on Unity scenes. "
        "Read-only actions: get_hierarchy, get_active, get_build_settings, get_loaded_scenes, scene_view_frame. "
        "Modifying actions: create (with optional template), load (with optional additive flag), save, "
        "close_scene, set_active_scene, move_to_scene, apply_external_edit, and validate when auto_repair is enabled. "
        "apply_external_edit rewrites a .unity file on disk and resyncs the Editor in one step, which is the "
        "only safe way to hand-patch scene YAML: doing it outside Unity raises a modal reload prompt that "
        "blocks the Editor's main thread and hangs this bridge until a human clicks it. "
        "get_hierarchy supports page_size, cursor, max_depth, and include_transform."
    ),
    annotations=ToolAnnotations(
        title="Manage Scene",
        destructiveHint=True,
    ),
)
async def manage_scene(
    ctx: Context,
    action: Annotated[Literal[
        "create",
        "load",
        "save",
        "get_hierarchy",
        "get_active",
        "get_build_settings",
        "scene_view_frame",
        "close_scene",
        "set_active_scene",
        "get_loaded_scenes",
        "move_to_scene",
        "apply_external_edit",
        "validate",
    ], "Perform CRUD operations on Unity scenes and control the Scene View camera."],
    name: Annotated[str, "Scene name."] | None = None,
    path: Annotated[str, "Scene path."] | None = None,
    build_index: Annotated[int | str,
                           "Unity build index (quote as string, e.g., '0')."] | None = None,
    # --- scene_view_frame params ---
    scene_view_target: Annotated[str | int,
                                 "GameObject reference for scene_view_frame (name, path, or instance ID)."] | None = None,
    # --- get_hierarchy paging/safety ---
    parent: Annotated[str | int,
                      "Optional parent GameObject reference (name/path/instanceID) to list direct children."] | None = None,
    page_size: Annotated[int | str,
                         "Page size for get_hierarchy paging."] | None = None,
    cursor: Annotated[int | str,
                      "Opaque cursor for paging (offset)."] | None = None,
    max_nodes: Annotated[int | str,
                         "Hard cap on returned nodes per request (safety)."] | None = None,
    max_depth: Annotated[int | str,
                         "Accepted for forward-compatibility; current paging returns a single level."] | None = None,
    max_children_per_node: Annotated[int | str,
                                     "Child paging hint (safety)."] | None = None,
    include_transform: Annotated[bool | str,
                                 "If true, include local transform in node summaries."] | None = None,
    # --- Multi-scene editing params ---
    scene_name: Annotated[str,
                          "Scene name for multi-scene operations."] | None = None,
    scene_path: Annotated[str,
                          "Full scene path (e.g. 'Assets/Scenes/Level2.unity')."] | None = None,
    target: Annotated[str | int,
                      "GameObject reference (name, path, or instanceID) for move_to_scene."] | None = None,
    remove_scene: Annotated[bool | str,
                            "For close_scene: true to fully remove, false to just unload."] | None = None,
    additive: Annotated[bool | str,
                        "For load: true to open scene additively (keeps current scene)."] | None = None,
    # --- Scene template ---
    template: Annotated[str,
                        "For create: scene template ('empty', 'default', '3d_basic', '2d_basic'). Omit for empty scene."] | None = None,
    # --- Scene validation ---
    auto_repair: Annotated[bool | str,
                           "For validate: true to auto-fix missing scripts (undoable)."] | None = None,
    # --- apply_external_edit ---
    edits: Annotated[list[dict[str, Any]],
                     "For apply_external_edit: literal replacements applied to the scene file, each "
                     "{'old_text': ..., 'new_text': ..., 'count': 1}. Every anchor must match exactly "
                     "'count' times or nothing is written. Omit for a pure discard-and-reload of a file "
                     "already changed on disk."] | None = None,
    discard_unsaved: Annotated[bool | str,
                               "For apply_external_edit: true to drop the open scene's unsaved in-memory "
                               "changes, which the rewrite would otherwise refuse to discard."] | None = None,
    dry_run: Annotated[bool | str,
                       "For apply_external_edit: true to report which anchors matched without writing."] | None = None,
) -> dict[str, Any]:
    unity_instance = await get_unity_instance_from_context(ctx)
    # apply_external_edit must not be preceded by a refresh: a refresh is exactly what makes Unity
    # notice the on-disk change and raise the modal prompt this action exists to avoid.
    gate = await preflight(
        ctx,
        wait_for_no_compile=True,
        refresh_if_dirty=action != "apply_external_edit",
    )
    if gate is not None:
        return gate.model_dump()
    try:
        coerced_build_index = coerce_int(build_index, default=None)
        coerced_page_size = coerce_int(page_size, default=None)
        coerced_cursor = coerce_int(cursor, default=None)
        coerced_max_nodes = coerce_int(max_nodes, default=None)
        coerced_max_depth = coerce_int(max_depth, default=None)
        coerced_max_children_per_node = coerce_int(
            max_children_per_node, default=None)
        coerced_include_transform = coerce_bool(
            include_transform, default=None)

        params: dict[str, Any] = {"action": action}
        if name:
            params["name"] = name
        if path:
            params["path"] = path
        if coerced_build_index is not None:
            params["buildIndex"] = coerced_build_index

        # scene_view_frame params
        if scene_view_target is not None:
            params["sceneViewTarget"] = scene_view_target

        # get_hierarchy paging/safety params (optional)
        if parent is not None:
            params["parent"] = parent
        if coerced_page_size is not None:
            params["pageSize"] = coerced_page_size
        if coerced_cursor is not None:
            params["cursor"] = coerced_cursor
        if coerced_max_nodes is not None:
            params["maxNodes"] = coerced_max_nodes
        if coerced_max_depth is not None:
            params["maxDepth"] = coerced_max_depth
        if coerced_max_children_per_node is not None:
            params["maxChildrenPerNode"] = coerced_max_children_per_node
        if coerced_include_transform is not None:
            params["includeTransform"] = coerced_include_transform

        # Multi-scene editing params
        if scene_name is not None:
            params["sceneName"] = scene_name
        if scene_path is not None:
            params["scenePath"] = scene_path
        if target is not None:
            params["target"] = target
        coerced_remove_scene = coerce_bool(remove_scene, default=None)
        if coerced_remove_scene is not None:
            params["removeScene"] = coerced_remove_scene
        coerced_additive = coerce_bool(additive, default=None)
        if coerced_additive is not None:
            params["additive"] = coerced_additive
        # Scene template
        if template is not None:
            params["template"] = template

        # Scene validation
        coerced_auto_repair = coerce_bool(auto_repair, default=None)
        if coerced_auto_repair is not None:
            params["autoRepair"] = coerced_auto_repair

        # apply_external_edit
        if edits is not None:
            params["edits"] = edits
        coerced_discard_unsaved = coerce_bool(discard_unsaved, default=None)
        if coerced_discard_unsaved is not None:
            params["discard_unsaved"] = coerced_discard_unsaved
        coerced_dry_run = coerce_bool(dry_run, default=None)
        if coerced_dry_run is not None:
            params["dry_run"] = coerced_dry_run

        # Use centralized retry helper with instance routing
        response = await send_with_unity_instance(async_send_command_with_retry, unity_instance, "manage_scene", params)

        # Preserve structured failure data; unwrap success into a friendlier shape
        if isinstance(response, dict) and response.get("success"):
            friendly = {"success": True, "message": response.get("message", "Scene operation successful."), "data": response.get("data")}
            if response.get("queue") is not None:
                friendly["queue"] = response["queue"]
            return friendly
        return response if isinstance(response, dict) else {"success": False, "message": str(response)}

    except Exception as e:
        return {"success": False, "message": f"Python error managing scene: {str(e)}"}
