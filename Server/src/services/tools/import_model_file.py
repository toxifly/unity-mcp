"""
Defines the import_model_file tool: import a local 3D model file (already on disk,
e.g. exported from Blender) into the Unity project.

Thin pass-through: NO API keys and NO file bytes cross the bridge. The C# side copies
the file under Assets/ and runs the shared model-import pipeline.
"""
from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry


@mcp_for_unity_tool(
    group="asset_gen",
    description=(
        "Copy a local FBX, OBJ, glTF/GLB, or ZIP model into Assets/ and run Unity's model importer. "
        "source_path identifies the local file; name, output_folder, target_size, and animation_type "
        "configure the import. Use generic or humanoid animation_type for rigged FBX/OBJ clips; none "
        "imports no rig, and glTF/GLB ignore this setting because glTFast handles animation. glTF/GLB "
        "requires glTFast. ZIP multi-file exports so external .bin, .mtl, and texture sidecars are copied. "
        "The operation writes project assets but sends no source-file bytes or API keys over the bridge."
    ),
    annotations=ToolAnnotations(
        title="Import Model File",
        destructiveHint=False,
    ),
)
async def import_model_file(
    ctx: Context,
    source_path: Annotated[str, "Path to the model file on disk (.fbx/.obj/.glb/.gltf/.zip)."],
    name: Annotated[str, "Base name for the imported asset."] | None = None,
    output_folder: Annotated[str, "Destination folder under Assets/ for the import."] | None = None,
    target_size: Annotated[float, "Normalize the largest dimension to this size (meters)."] | None = None,
    animation_type: Annotated[
        Literal["none", "generic", "humanoid", "legacy"],
        "FBX/OBJ only: rig/animation import mode. 'generic' or 'humanoid' surface the model's "
        "AnimationClips; 'legacy' selects Unity's legacy Animation system (rarely needed); "
        "omitted or 'none' imports no rig. Ignored for glTF/GLB.",
    ] | None = None,
) -> dict[str, Any]:
    unity_instance = await get_unity_instance_from_context(ctx)

    params_dict = {
        "sourcePath": source_path,
        "name": name,
        "outputFolder": output_folder,
        "targetSize": target_size,
        "animationType": animation_type,
    }
    params_dict = {k: v for k, v in params_dict.items() if v is not None}

    result = await send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "import_model_file",
        params_dict,
    )

    return result if isinstance(result, dict) else {"success": False, "message": str(result)}
