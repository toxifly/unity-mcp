"""Transaction-backed, path-scoped Unity scene and asset saves."""

from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations
from pydantic import Field

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from services.tools.preflight import preflight
from transport.legacy.unity_connection import async_send_command_with_retry
from transport.unity_transport import send_with_unity_instance


async def _send(
    ctx: Context,
    command: str,
    params: dict[str, Any],
    *,
    refresh_if_dirty: bool = True,
) -> dict[str, Any]:
    gate = await preflight(
        ctx,
        wait_for_no_compile=True,
        refresh_if_dirty=refresh_if_dirty,
    )
    if gate is not None:
        return gate.model_dump()
    instance = await get_unity_instance_from_context(ctx)
    result = await send_with_unity_instance(
        async_send_command_with_retry, instance, command, params
    )
    return result if isinstance(result, dict) else {"success": False, "message": str(result)}


@mcp_for_unity_tool(
    group="core",
    description=(
        "Save exactly one loaded Unity scene through the mutation transaction boundary. "
        "The save rolls back if serialization introduces property changes beyond the pre-save state."
    ),
    annotations=ToolAnnotations(title="Save Scene Scoped", destructiveHint=True),
)
async def save_scene_scoped(
    ctx: Context,
    scene_path: Annotated[str | None, Field(description="Project-relative path of a loaded scene.")] = None,
    scene_name: Annotated[str | None, Field(description="Name of a loaded scene; use scene_path when names are ambiguous.")] = None,
    dirty_scene_policy: Annotated[
        Literal["reject", "preserve", "allow"],
        Field(description="How to handle a scene that is already dirty. Saving defaults to allow."),
    ] = "allow",
) -> dict[str, Any]:
    if not scene_path and not scene_name:
        return {"success": False, "message": "Provide scene_path or scene_name."}
    params: dict[str, Any] = {"dirtyScenePolicy": dirty_scene_policy}
    if scene_path:
        params["scenePath"] = scene_path
    if scene_name:
        params["sceneName"] = scene_name
    return await _send(ctx, "save_scene_scoped", params)


@mcp_for_unity_tool(
    group="core",
    description="Save exactly one prefab asset with transaction-backed save-time change detection and rollback.",
    annotations=ToolAnnotations(title="Save Prefab Scoped", destructiveHint=True),
)
async def save_prefab_scoped(
    ctx: Context,
    prefab_path: Annotated[str, Field(description="Project-relative .prefab asset path under Assets/.")],
) -> dict[str, Any]:
    if not prefab_path:
        return {"success": False, "message": "Provide prefab_path."}
    return await _send(ctx, "save_prefab_scoped", {"prefabPath": prefab_path})


@mcp_for_unity_tool(
    group="core",
    description=(
        "Save only the declared Unity asset paths. Other dirty scenes and assets are left unsaved and reported."
    ),
    annotations=ToolAnnotations(title="Save Assets Scoped", destructiveHint=True),
)
async def save_assets_scoped(
    ctx: Context,
    asset_paths: Annotated[
        list[str] | str,
        Field(description="One or more project-relative authored asset paths under Assets/."),
    ],
) -> dict[str, Any]:
    if isinstance(asset_paths, str):
        asset_paths = [asset_paths]
    if not asset_paths:
        return {"success": False, "message": "Provide at least one asset path."}
    return await _send(ctx, "save_assets_scoped", {"assetPaths": asset_paths})


@mcp_for_unity_tool(
    group="core",
    description=(
        "Preview dirty objects for declared asset paths or one loaded scene without saving. "
        "Returns the same bounded scoped-save report shape with assets_saved empty."
    ),
    annotations=ToolAnnotations(title="Preview Asset Changes", readOnlyHint=True),
)
async def preview_asset_changes(
    ctx: Context,
    asset_paths: Annotated[list[str] | str | None, Field(description="Authored asset paths to preview.")] = None,
    scene_path: Annotated[str | None, Field(description="Loaded scene path to preview.")] = None,
    scene_name: Annotated[str | None, Field(description="Loaded scene name to preview.")] = None,
) -> dict[str, Any]:
    if isinstance(asset_paths, str):
        asset_paths = [asset_paths]
    if not asset_paths and not scene_path and not scene_name:
        return {"success": False, "message": "Provide asset_paths, scene_path, or scene_name."}
    if asset_paths and (scene_path or scene_name):
        return {"success": False, "message": "Preview assets or one scene per call, not both."}
    params: dict[str, Any] = {}
    if asset_paths:
        params["assetPaths"] = asset_paths
    if scene_path:
        params["scenePath"] = scene_path
    if scene_name:
        params["sceneName"] = scene_name
    return await _send(
        ctx,
        "preview_asset_changes",
        params,
        refresh_if_dirty=False,
    )
