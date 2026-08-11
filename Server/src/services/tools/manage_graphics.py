from typing import Annotated, Any, Optional

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry

VOLUME_ACTIONS = [
    "volume_create", "volume_add_effect", "volume_set_effect",
    "volume_remove_effect", "volume_get_info", "volume_set_properties",
    "volume_list_effects", "volume_create_profile",
]

BAKE_ACTIONS = [
    "bake_start", "bake_cancel", "bake_status", "bake_clear",
    "bake_reflection_probe", "bake_get_settings", "bake_set_settings",
    "bake_create_light_probe_group", "bake_create_reflection_probe",
    "bake_set_probe_positions",
]

STATS_ACTIONS = [
    "stats_get", "stats_list_counters", "stats_set_scene_debug", "stats_get_memory",
]

PIPELINE_ACTIONS = [
    "pipeline_get_info", "pipeline_set_quality",
    "pipeline_get_settings", "pipeline_set_settings",
]

FEATURE_ACTIONS = [
    "feature_list", "feature_add", "feature_remove",
    "feature_configure", "feature_toggle", "feature_reorder",
]

SKYBOX_ACTIONS = [
    "skybox_get", "skybox_set_material", "skybox_set_properties",
    "skybox_set_ambient", "skybox_set_fog", "skybox_set_reflection",
    "skybox_set_sun",
]

ALL_ACTIONS = (
    ["ping"] + VOLUME_ACTIONS + BAKE_ACTIONS + STATS_ACTIONS
    + PIPELINE_ACTIONS + FEATURE_ACTIONS + SKYBOX_ACTIONS
)


@mcp_for_unity_tool(
    group="core",
    description=(
        "Manage volumes and post-processing, light baking, rendering statistics, render-pipeline settings, "
        "URP renderer features, and skybox/environment settings. action prefixes select the domain: "
        "volume_*, bake_*, stats_*, pipeline_*, feature_*, or skybox_*; ping reports the active pipeline "
        "and supported features. target, effect, parameters, properties, settings, and the remaining "
        "domain-specific fields supply action inputs. Volume actions require URP or HDRP, renderer-feature "
        "actions require URP, and baking runs only in Edit mode. get/list/status actions are read-only; "
        "create, set, add, remove, configure, toggle, reorder, bake, and clear actions mutate scene assets "
        "or project rendering settings."
    ),
    annotations=ToolAnnotations(title="Manage Graphics", destructiveHint=True),
)
async def manage_graphics(
    ctx: Context,
    action: Annotated[str, "The graphics action to perform."],
    target: Annotated[Optional[str], "Target object name or instance ID."] = None,
    effect: Annotated[Optional[str], "Effect type name (e.g., 'Bloom', 'Vignette')."] = None,
    parameters: Annotated[Optional[dict[str, Any]], "Dict of parameter values."] = None,
    properties: Annotated[Optional[dict[str, Any]], "Dict of properties to set."] = None,
    settings: Annotated[Optional[dict[str, Any]], "Dict of settings (bake/pipeline)."] = None,
    name: Annotated[Optional[str], "Name for created objects."] = None,
    is_global: Annotated[Optional[bool], "Whether Volume is global (default true)."] = None,
    weight: Annotated[Optional[float], "Volume weight (0-1)."] = None,
    priority: Annotated[Optional[float], "Volume priority."] = None,
    profile_path: Annotated[Optional[str], "Asset path for VolumeProfile."] = None,
    effects: Annotated[Optional[list[dict[str, Any]]], "Effect definitions for volume_create."] = None,
    path: Annotated[Optional[str], "Asset path for volume_create_profile."] = None,
    level: Annotated[Optional[str], "Quality level name or index."] = None,
    position: Annotated[Optional[list[float]], "Position [x,y,z]."] = None,
    grid_size: Annotated[Optional[list[int]], "Probe grid size [x,y,z]."] = None,
    spacing: Annotated[Optional[float], "Probe grid spacing."] = None,
    size: Annotated[Optional[list[float]], "Probe/volume size [x,y,z]."] = None,
    resolution: Annotated[Optional[int], "Probe resolution."] = None,
    mode: Annotated[Optional[str], "Probe mode or debug mode."] = None,
    hdr: Annotated[Optional[bool], "HDR for reflection probes."] = None,
    box_projection: Annotated[Optional[bool], "Box projection for reflection probes."] = None,
    positions: Annotated[Optional[list[list[float]]], "Probe positions array."] = None,
    index: Annotated[Optional[int], "Feature index."] = None,
    active: Annotated[Optional[bool], "Feature active state."] = None,
    order: Annotated[Optional[list[int]], "Feature reorder indices."] = None,
    # bake_start
    async_bake: Annotated[Optional[bool], "Async bake (default true)."] = None,
    # feature_add
    feature_type: Annotated[Optional[str], "Renderer feature type name."] = None,
    material: Annotated[Optional[str], "Material asset path for feature."] = None,
    # skybox / environment
    color: Annotated[Optional[list[float]], "Color [r,g,b,a] for ambient/fog."] = None,
    intensity: Annotated[Optional[float], "Intensity value (ambient/reflection)."] = None,
    ambient_mode: Annotated[Optional[str], "Ambient mode: Skybox, Trilight, Flat, Custom."] = None,
    equator_color: Annotated[Optional[list[float]], "Equator color [r,g,b,a] (Trilight mode)."] = None,
    ground_color: Annotated[Optional[list[float]], "Ground color [r,g,b,a] (Trilight mode)."] = None,
    fog_enabled: Annotated[Optional[bool], "Enable or disable fog."] = None,
    fog_mode: Annotated[Optional[str], "Fog mode: Linear, Exponential, ExponentialSquared."] = None,
    fog_color: Annotated[Optional[list[float]], "Fog color [r,g,b,a]."] = None,
    fog_density: Annotated[Optional[float], "Fog density (Exponential modes)."] = None,
    fog_start: Annotated[Optional[float], "Fog start distance (Linear mode)."] = None,
    fog_end: Annotated[Optional[float], "Fog end distance (Linear mode)."] = None,
    bounces: Annotated[Optional[int], "Reflection bounces."] = None,
    reflection_mode: Annotated[Optional[str], "Default reflection mode: Skybox, Custom."] = None,
) -> dict[str, Any]:
    action_lower = action.lower()
    if action_lower not in ALL_ACTIONS:
        return {
            "success": False,
            "message": f"Unknown action '{action}'. Valid actions: {', '.join(ALL_ACTIONS)}",
        }

    unity_instance = await get_unity_instance_from_context(ctx)

    params_dict: dict[str, Any] = {"action": action_lower}

    # Map all non-None params
    param_map = {
        "target": target, "effect": effect, "parameters": parameters,
        "properties": properties, "settings": settings, "name": name,
        "is_global": is_global, "weight": weight, "priority": priority,
        "profile_path": profile_path, "effects": effects, "path": path,
        "level": level, "position": position, "grid_size": grid_size,
        "spacing": spacing, "size": size, "resolution": resolution,
        "mode": mode, "hdr": hdr, "box_projection": box_projection,
        "positions": positions, "index": index, "active": active,
        "order": order, "async": async_bake, "type": feature_type,
        "material": material, "color": color, "intensity": intensity,
        "ambient_mode": ambient_mode, "equator_color": equator_color,
        "ground_color": ground_color, "fog_enabled": fog_enabled,
        "fog_mode": fog_mode, "fog_color": fog_color,
        "fog_density": fog_density, "fog_start": fog_start,
        "fog_end": fog_end, "bounces": bounces,
        "reflection_mode": reflection_mode,
    }
    for key, val in param_map.items():
        if val is not None:
            params_dict[key] = val

    result = await send_with_unity_instance(
        async_send_command_with_retry, unity_instance, "manage_graphics", params_dict
    )
    return result if isinstance(result, dict) else {"success": False, "message": str(result)}
