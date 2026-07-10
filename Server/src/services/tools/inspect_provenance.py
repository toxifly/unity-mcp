"""Read compact scene and prefab provenance without serialized component dumps."""

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
        "Inspect compact scene and prefab provenance for hierarchy objects, assets, or "
        "GlobalObjectIds. Reports stable identity, scene/asset paths, instance roots, "
        "source prefab objects, property override paths, and added/removed components "
        "without dumping serialized component values."
    ),
)
async def inspect_provenance(
    ctx: Context,
    targets: Annotated[
        list[str] | str,
        Field(description="Hierarchy name/path, asset path, or GlobalObjectId to inspect."),
    ],
    include_property_overrides: Annotated[
        bool,
        Field(default=True, description="Include property override paths scoped to each target object."),
    ] = True,
    include_component_overrides: Annotated[
        bool,
        Field(default=True, description="Include added and removed component state scoped to each target object."),
    ] = True,
    include_inactive: Annotated[
        bool,
        Field(default=True, description="Resolve inactive authored scene objects."),
    ] = True,
    override_limit: Annotated[
        int,
        Field(default=20, ge=1, le=200, description="Maximum property override records per target (1-200)."),
    ] = 20,
    component_limit: Annotated[
        int,
        Field(default=10, ge=1, le=100, description="Maximum added and removed component records per target (1-100 each)."),
    ] = 10,
    page_size: Annotated[
        int,
        Field(default=10, ge=1, le=200, description="Maximum target provenance findings returned (1-200)."),
    ] = 10,
    cursor: Annotated[
        str | None,
        Field(default=None, description="Opaque cursor returned by a previous call."),
    ] = None,
) -> dict[str, Any]:
    if isinstance(targets, str):
        targets = [targets]
    if not targets or any(not item.strip() for item in targets):
        return {"success": False, "message": "Provide at least one non-empty target."}
    if len(targets) > 200:
        return {"success": False, "message": "At most 200 targets may be inspected per request."}
    nested_records_per_target = (
        (override_limit if include_property_overrides else 0)
        + (component_limit * 2 if include_component_overrides else 0)
    )
    if page_size * nested_records_per_target > 500:
        return {
            "success": False,
            "message": "The requested page and nested provenance limits must not exceed 500 records.",
        }

    params: dict[str, Any] = {
        "targets": targets,
        "includePropertyOverrides": include_property_overrides,
        "includeComponentOverrides": include_component_overrides,
        "includeInactive": include_inactive,
        "overrideLimit": override_limit,
        "componentLimit": component_limit,
        "pageSize": page_size,
    }
    if cursor is not None:
        params["cursor"] = cursor

    unity_instance = await get_unity_instance_from_context(ctx)
    try:
        response = await send_with_unity_instance(
            async_send_command_with_retry,
            unity_instance,
            "inspect_provenance",
            params,
        )
        if isinstance(response, dict):
            return response
        return {"success": False, "message": str(response)}
    except Exception as exc:
        return {"success": False, "message": f"Error inspecting provenance: {exc!s}"}
