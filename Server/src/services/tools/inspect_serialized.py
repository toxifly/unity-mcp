"""Read selected Unity serialized properties without dumping whole components."""

from typing import Annotated, Any

from fastmcp import Context
from pydantic import Field

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.legacy.unity_connection import async_send_command_with_retry
from transport.unity_transport import send_with_unity_instance


@mcp_for_unity_tool(
    group="core",
    description=(
        "Inspect an explicit whitelist of Unity serialized properties using SerializedObject. "
        "Targets may be hierarchy names/paths, GlobalObjectIds, or objects with 'target' and "
        "an optional 'component' type filter. Reports null and broken object references "
        "distinctly and can include compact prefab source/override provenance. Set prefab_path "
        "to scope name/path/GlobalObjectId targets to an Assets/ or Packages/ prefab. A matching "
        "open Prefab Stage is reused so unsaved edits are inspected."
    ),
)
async def inspect_serialized(
    ctx: Context,
    targets: Annotated[
        list[str | dict[str, str]] | str,
        Field(description="Target name/path/GlobalObjectId, or target objects with an optional component filter."),
    ],
    properties: Annotated[
        list[str] | str,
        Field(description="Exact SerializedProperty paths to inspect; whole-component dumps are not supported."),
    ],
    component_type: Annotated[
        str | None,
        Field(default=None, description="Optional component type filter applied to string targets."),
    ] = None,
    include_prefab_provenance: Annotated[
        bool,
        Field(default=True, description="Include prefab source asset and property override state."),
    ] = True,
    include_missing_references: Annotated[
        bool,
        Field(default=True, description="Include broken object-reference findings (null references are always returned)."),
    ] = True,
    include_inactive: Annotated[
        bool,
        Field(default=True, description="Resolve inactive authored scene objects."),
    ] = True,
    page_size: Annotated[
        int,
        Field(default=50, ge=1, le=200, description="Maximum findings returned (1-200)."),
    ] = 50,
    cursor: Annotated[
        str | None,
        Field(default=None, description="Opaque cursor returned by a previous call."),
    ] = None,
    prefab_path: Annotated[
        str | None,
        Field(default=None, description="Optional project-relative .prefab asset path under Assets/ or Packages/. Name/path/GlobalObjectId targets are resolved only within this prefab."),
    ] = None,
) -> dict[str, Any]:
    if isinstance(targets, str):
        targets = [targets]
    if isinstance(properties, str):
        properties = [properties]
    if not targets:
        return {"success": False, "message": "Provide at least one target."}
    if not properties or any(not item.strip() for item in properties):
        return {"success": False, "message": "Provide at least one non-empty serialized property path."}

    params: dict[str, Any] = {
        "targets": targets,
        "properties": properties,
        "includePrefabProvenance": include_prefab_provenance,
        "includeMissingReferences": include_missing_references,
        "includeInactive": include_inactive,
        "pageSize": page_size,
    }
    if prefab_path is not None:
        params["prefabPath"] = prefab_path
    if component_type:
        params["componentType"] = component_type
    if cursor is not None:
        params["cursor"] = cursor

    unity_instance = await get_unity_instance_from_context(ctx)
    try:
        response = await send_with_unity_instance(
            async_send_command_with_retry,
            unity_instance,
            "inspect_serialized",
            params,
        )
        if isinstance(response, dict):
            return response
        return {"success": False, "message": str(response)}
    except Exception as exc:
        return {"success": False, "message": f"Error inspecting serialized properties: {exc!s}"}
