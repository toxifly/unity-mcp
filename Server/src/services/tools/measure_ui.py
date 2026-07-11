"""
measure_ui — read-only bounds measurement for uGUI (Canvas / RectTransform) layouts.

Returns the canvas-local and/or screen-space rectangle of named GameObjects (and
optionally their immediate RectTransform children) so UI layout work can be verified
numerically — clearances, overlaps, clipping — without a Game View screenshot. A
screenshot is large, costs many tokens, and returns white when the view is unfocused;
measuring a handful of rects is small, deterministic, and always works. Use this for the
iteration loop and leave the "does it look right" judgement to a human viewing the live
Game View.
"""
from typing import Annotated, Any, Literal

from fastmcp import Context
from pydantic import Field

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry


@mcp_for_unity_tool(
    group="core",
    description=(
        "Read uGUI RectTransform bounds without mutating scene state. Returns each target's "
        "rectangle in an explicit canvas, local, world, or screen-pixel coordinate space. "
        "Targets are GameObject names, or hierarchy paths ('Canvas/Panel/Button') for "
        "disambiguation. Set include_children to also measure each target's immediate "
        "RectTransform children. Inactive objects are "
        "included by default. Geometry assertions can be evaluated in the same call."
    )
)
async def measure_ui(
    ctx: Context,
    targets: Annotated[
        list[str] | str | None,
        Field(default=None, description="GameObject name(s) or hierarchy path(s) to measure.")
    ] = None,
    container: Annotated[
        str | None,
        Field(default=None, description="A GameObject whose immediate RectTransform children are all measured (plus the container itself).")
    ] = None,
    reference: Annotated[
        str | None,
        Field(default=None, description="GameObject whose RectTransform defines canvas-local space. Defaults to the first target's root Canvas.")
    ] = None,
    include_children: Annotated[
        bool | str | None,
        Field(default=None, description="Also measure each target's immediate RectTransform children (default false).")
    ] = None,
    include_inactive: Annotated[
        bool | str | None,
        Field(default=None, description="Measure inactive/hidden objects too (default true).")
    ] = None,
    space: Annotated[
        Literal["canvas", "local", "world", "screen_pixels"],
        Field(default="canvas", description="The single coordinate space used by all measurements and assertions.")
    ] = "canvas",
    assertions: Annotated[
        list[dict[str, Any]] | None,
        Field(default=None, description="Optional geometry assertions evaluated in the declared coordinate space.")
    ] = None,
) -> dict[str, Any]:
    unity_instance = await get_unity_instance_from_context(ctx)

    if isinstance(targets, str):
        targets = [targets]
    if not targets and not container:
        return {
            "success": False,
            "message": "Provide 'targets' (name(s)/path(s)) and/or 'container'.",
        }

    params: dict[str, Any] = {"space": space}
    if targets:
        params["targets"] = targets
    if container is not None:
        params["container"] = container
    if reference is not None:
        params["reference"] = reference
    if include_children is not None:
        params["includeChildren"] = include_children
    if include_inactive is not None:
        params["includeInactive"] = include_inactive
    if assertions is not None:
        params["assertions"] = assertions

    try:
        response = await send_with_unity_instance(
            async_send_command_with_retry,
            unity_instance,
            "measure_ui",
            params,
        )
        if isinstance(response, dict):
            return response
        return {"success": False, "message": str(response)}
    except Exception as e:
        return {"success": False, "message": f"Error measuring UI: {e!s}"}
