from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry

# All possible actions grouped by category
SHAPE_ACTIONS = [
    "create_shape", "create_poly_shape",
]

MESH_ACTIONS = [
    "extrude_faces", "extrude_edges", "bevel_edges", "subdivide",
    "delete_faces", "bridge_edges", "connect_elements", "detach_faces",
    "flip_normals", "merge_faces", "combine_meshes", "merge_objects",
    "duplicate_and_flip", "create_polygon",
]

VERTEX_ACTIONS = [
    "merge_vertices", "weld_vertices", "split_vertices", "move_vertices",
    "insert_vertex", "append_vertices_to_edge",
]

SELECTION_ACTIONS = [
    "select_faces",
]

UV_MATERIAL_ACTIONS = [
    "set_face_material", "set_face_color", "set_face_uvs",
]

QUERY_ACTIONS = [
    "get_mesh_info", "convert_to_probuilder",
]

SMOOTHING_ACTIONS = ["set_smoothing", "auto_smooth"]

UTILITY_ACTIONS = ["center_pivot", "freeze_transform", "set_pivot", "validate_mesh", "repair_mesh"]

ALL_ACTIONS = (
    ["ping"] + SHAPE_ACTIONS + MESH_ACTIONS + VERTEX_ACTIONS + SELECTION_ACTIONS
    + UV_MATERIAL_ACTIONS + QUERY_ACTIONS + SMOOTHING_ACTIONS + UTILITY_ACTIONS
)

@mcp_for_unity_tool(
    group="probuilder",
    description=(
        "Create, query, and edit ProBuilder meshes; requires com.unity.probuilder. action covers shape "
        "creation (create_shape, create_poly_shape), face/edge editing (extrude, bevel, subdivide, delete, "
        "bridge, connect, detach, flip, merge, combine, duplicate, create_polygon), vertex editing (merge, "
        "weld, split, move, insert, append), select_faces, face material/color/UV assignment, get_mesh_info, "
        "convert_to_probuilder, smoothing, pivot changes, freeze_transform, validate_mesh, and repair_mesh. "
        "target and search_method identify the object; properties carries action-specific indices, edges, "
        "vectors, shape settings, and materials. get_mesh_info and validate_mesh are read-only; every other "
        "action mutates scene objects. get_mesh_info include accepts summary, faces, edges, or all."
    ),
    annotations=ToolAnnotations(
        title="Manage ProBuilder",
        destructiveHint=True,
    ),
)
async def manage_probuilder(
    ctx: Context,
    action: Annotated[str, "Action to perform."],
    target: Annotated[str | None, "Target GameObject (name/path/id)."] = None,
    search_method: Annotated[
        Literal["by_id", "by_name", "by_path", "by_tag", "by_layer"] | None,
        "How to find the target GameObject.",
    ] = None,
    properties: Annotated[
        dict[str, Any] | str | None,
        "Action-specific parameters (dict or JSON string).",
    ] = None,
) -> dict[str, Any]:
    """Unified ProBuilder mesh management tool."""

    action_normalized = action.lower()

    if action_normalized not in ALL_ACTIONS:
        # Provide helpful category-based suggestions
        categories = {
            "Shape creation": SHAPE_ACTIONS,
            "Mesh editing": MESH_ACTIONS,
            "Vertex operations": VERTEX_ACTIONS,
            "Selection": SELECTION_ACTIONS,
            "UV & materials": UV_MATERIAL_ACTIONS,
            "Query": QUERY_ACTIONS,
            "Smoothing": SMOOTHING_ACTIONS,
            "Mesh utilities": UTILITY_ACTIONS,
        }
        category_list = "; ".join(
            f"{cat}: {', '.join(actions)}" for cat, actions in categories.items()
        )
        return {
            "success": False,
            "message": (
                f"Unknown action '{action}'. Available actions by category — {category_list}. "
                "Run with action='ping' to test connection."
            ),
        }

    unity_instance = await get_unity_instance_from_context(ctx)

    params_dict: dict[str, Any] = {"action": action_normalized}
    if properties is not None:
        params_dict["properties"] = properties
    if target is not None:
        params_dict["target"] = target
    if search_method is not None:
        params_dict["searchMethod"] = search_method

    params_dict = {k: v for k, v in params_dict.items() if v is not None}

    result = await send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "manage_probuilder",
        params_dict,
    )

    return result if isinstance(result, dict) else {"success": False, "message": str(result)}
