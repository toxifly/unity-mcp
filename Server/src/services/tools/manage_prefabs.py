from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from services.tools.utils import coerce_bool, normalize_vector3
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry
from services.tools.utils import coerce_bool, coerce_int, parse_json_payload
from services.tools.preflight import preflight


# Required parameters for each action
REQUIRED_PARAMS = {
    "get_info": ["prefab_path"],
    "get_hierarchy": ["prefab_path"],
    "create_from_gameobject": ["target", "prefab_path"],
    "create_and_replace": ["target", "prefab_path"],
    "modify_contents": ["prefab_path"],
    "open_prefab_stage": ["prefab_path"],
}


@mcp_for_unity_tool(
    description=(
        "Manages Unity Prefab assets. "
        "Read-only actions: get_info and get_hierarchy. Mutating actions: create_from_gameobject, create_and_replace, modify_contents, "
        "apply_instance_overrides, revert_instance_overrides, unpack_instance, "
        "open_prefab_stage, save_prefab_stage, close_prefab_stage. "
        "modify_contents edits an asset without opening Prefab Stage; open_prefab_stage, save_prefab_stage, "
        "and close_prefab_stage control interactive stage state. create_and_replace performs guarded prefab "
        "creation with optional scene replacement. With modify_contents, create_child and delete_child accept "
        "one item or an array, and component_properties sets serialized fields on existing components. "
        "Object references accept guid, Assets path, or instanceID forms."
    ),
    annotations=ToolAnnotations(
        title="Manage Prefabs",
        destructiveHint=True,
    ),
)
async def manage_prefabs(
    ctx: Context,
    action: Annotated[
        Literal[
            "create_from_gameobject",
            "create_and_replace",
            "get_info",
            "get_hierarchy",
            "modify_contents",
            "apply_instance_overrides",
            "revert_instance_overrides",
            "unpack_instance",
            "open_prefab_stage",
            "save_prefab_stage",
            "close_prefab_stage",
        ],
        "Prefab operation to perform.",
    ],
    prefab_path: Annotated[str, "Prefab asset path (e.g., Assets/Prefabs/MyPrefab.prefab)."] | None = None,
    target: Annotated[dict[str, Any] | str | int,
                      "Target GameObject reference. Accepts instance ID, name, path, or object like {\"instanceID\": 123} / {\"name\": \"Player\"} / {\"path\": \"/Canvas/Panel\"}."] | None = None,
    search_method: Annotated[Literal["by_id", "by_name", "by_path"],
                             "How to resolve the target (optional)."] | None = None,
    allow_overwrite: Annotated[bool | str, "Allow replacing existing prefab."] | None = None,
    search_inactive: Annotated[bool | str, "Include inactive GameObjects in search."] | None = None,
    unlink_if_instance: Annotated[bool, "Unlink from existing prefab before creating new one."] | None = None,
    preserve_world_transform: Annotated[bool | str, "For create_and_replace, preserve and validate the source hierarchy's world transform."] | None = None,
    preserve_scene_references: Annotated[bool | str, "For create_and_replace, validate that external scene references into the hierarchy remain intact."] | None = None,
    save_scene: Annotated[bool | str, "For create_and_replace, save the owning scene after linking the prefab instance."] | None = None,
    link_scene_instance: Annotated[bool | str, "For create_and_replace, connect the source hierarchy to the new prefab; false creates only the asset."] | None = None,
    dirty_scene_policy: Annotated[Literal["reject", "preserve", "allow"], "Dirty-scene policy for create_and_replace. Defaults to reject; dry runs preserve prior dirtiness."] | None = None,
    change_guard: Annotated[dict[str, Any], "Expected serialized change scope for create_and_replace."] | None = None,
    dry_run: Annotated[bool | str, "Preview create_and_replace changes and roll back the scene and prefab asset."] | None = None,
    unpack_mode: Annotated[str,
                           "For unpack_instance: unpack mode. Valid values: OutermostRoot, Completely."] | None = None,
    # modify_contents parameters
    position: Annotated[list[float] | dict[str, float] | str, "New local position [x, y, z] or {x, y, z} for modify_contents."] | None = None,
    rotation: Annotated[list[float] | dict[str, float] | str, "New local rotation (euler angles) [x, y, z] or {x, y, z} for modify_contents."] | None = None,
    scale: Annotated[list[float] | dict[str, float] | str, "New local scale [x, y, z] or {x, y, z} for modify_contents."] | None = None,
    name: Annotated[str, "New name for the target object in modify_contents."] | None = None,
    tag: Annotated[str, "New tag for the target object in modify_contents."] | None = None,
    layer: Annotated[str, "New layer name for the target object in modify_contents."] | None = None,
    set_active: Annotated[bool, "Set active state of target object in modify_contents."] | None = None,
    parent: Annotated[str, "New parent object name/path within prefab for modify_contents."] | None = None,
    components_to_add: Annotated[list[str], "Component types to add in modify_contents."] | None = None,
    components_to_remove: Annotated[list[str], "Component types to remove in modify_contents."] | None = None,
    create_child: Annotated[dict[str, Any] | list[dict[str, Any]], "Create child GameObject(s) in the prefab. Single object or array of objects, each with: name (required), parent (optional, defaults to target), source_prefab_path (optional: asset path to instantiate as nested prefab, e.g. 'Assets/Prefabs/Bullet.prefab'), primitive_type (optional: Cube, Sphere, Capsule, Cylinder, Plane, Quad), position, rotation, scale, components_to_add, tag, layer, set_active. source_prefab_path and primitive_type are mutually exclusive."] | None = None,
    delete_child: Annotated[str | list[str], "Child name(s) or path(s) to remove from the prefab. Supports single string or array for batch deletion (e.g. 'Child1' or ['Child1', 'Child1/Grandchild'])."] | None = None,
    component_properties: Annotated[dict[str, dict[str, Any]], "Set properties on existing components in modify_contents. Keys are component type names, values are dicts of property name to value. Example: {\"Rigidbody\": {\"mass\": 5.0}, \"MyScript\": {\"health\": 100}}. Supports object references via {\"guid\": \"...\"}, {\"path\": \"Assets/...\"}, or {\"instanceID\": 123}. For Sprite sub-assets: {\"guid\": \"...\", \"spriteName\": \"<name>\"}. Single-sprite textures auto-resolve."] | None = None,
) -> dict[str, Any]:
    # Back-compat: map 'name' → 'target' for create_from_gameobject (Unity accepts both)
    if action == "create_from_gameobject" and target is None and name is not None:
        target = name

    # Validate required parameters
    required = REQUIRED_PARAMS.get(action, [])
    for param_name in required:
        # Use updated local value for target after back-compat mapping
        param_value = target if param_name == "target" else locals().get(param_name)
        # Check for None and empty/whitespace strings
        if param_value is None or (isinstance(param_value, str) and not param_value.strip()):
            return {
                "success": False,
                "message": f"Action '{action}' requires parameter '{param_name}'."
            }

    unity_instance = await get_unity_instance_from_context(ctx)

    # Preflight check for operations to ensure Unity is ready
    try:
        gate = await preflight(ctx, wait_for_no_compile=True, refresh_if_dirty=True)
        if gate is not None:
            return gate.model_dump()
    except Exception as exc:
        return {
            "success": False,
            "message": f"Unity preflight check failed: {exc}"
        }

    try:
        # Build parameters dictionary
        params: dict[str, Any] = {"action": action}

        # Handle prefab path parameter
        if prefab_path:
            params["prefabPath"] = prefab_path

        # Normalize target references: {instanceID|name|path}
        if target is not None:
            raw_target = parse_json_payload(target)
            inferred_method: str | None = None
            normalized_target: Any = raw_target

            if isinstance(raw_target, dict):
                instance_id = raw_target.get("instanceID") or raw_target.get("instance_id") or raw_target.get("id")
                if instance_id is not None:
                    normalized_target = coerce_int(instance_id, default=None)
                    inferred_method = "by_id"
                elif isinstance(raw_target.get("path"), str) and raw_target["path"].strip():
                    p = raw_target["path"].strip()
                    if p.startswith("/"):
                        p = p[1:]
                    normalized_target = p
                    inferred_method = "by_path"
                elif isinstance(raw_target.get("name"), str) and raw_target["name"].strip():
                    normalized_target = raw_target["name"].strip()
                    inferred_method = "by_name"
                else:
                    return {"success": False, "message": "Invalid target object: expected one of {instanceID,name,path}."}

            elif isinstance(raw_target, int):
                inferred_method = "by_id"
            elif isinstance(raw_target, str):
                s = raw_target.strip()
                if s.startswith("/"):
                    normalized_target = s[1:]
                    inferred_method = "by_path"
                elif "/" in s:
                    inferred_method = "by_path"
                else:
                    inferred_method = "by_name"

            params["target"] = normalized_target

            effective_method = search_method or inferred_method
            if effective_method:
                params["searchMethod"] = effective_method

        allow_overwrite_val = coerce_bool(allow_overwrite)
        if allow_overwrite_val is not None:
            params["allowOverwrite"] = allow_overwrite_val

        search_inactive_val = coerce_bool(search_inactive)
        if search_inactive_val is not None:
            params["searchInactive"] = search_inactive_val

        if unpack_mode and action == "unpack_instance":
            params["unpackMode"] = unpack_mode

        unlink_if_instance_val = coerce_bool(unlink_if_instance)
        if unlink_if_instance_val is not None:
            params["unlinkIfInstance"] = unlink_if_instance_val

        if action == "create_and_replace":
            for field_name, value in (
                ("preserveWorldTransform", preserve_world_transform),
                ("preserveSceneReferences", preserve_scene_references),
                ("saveScene", save_scene),
                ("linkSceneInstance", link_scene_instance),
                ("dryRun", dry_run),
            ):
                coerced = coerce_bool(value)
                if coerced is not None:
                    params[field_name] = coerced
            if dirty_scene_policy is not None:
                params["dirtyScenePolicy"] = dirty_scene_policy
            if change_guard is not None:
                params["changeGuard"] = change_guard

        # modify_contents parameters
        if position is not None:
            position_value, position_error = normalize_vector3(position, "position")
            if position_error:
                return {"success": False, "message": position_error}
            params["position"] = position_value
        if rotation is not None:
            rotation_value, rotation_error = normalize_vector3(rotation, "rotation")
            if rotation_error:
                return {"success": False, "message": rotation_error}
            params["rotation"] = rotation_value
        if scale is not None:
            scale_value, scale_error = normalize_vector3(scale, "scale")
            if scale_error:
                return {"success": False, "message": scale_error}
            params["scale"] = scale_value
        if name is not None:
            params["name"] = name
        if tag is not None:
            params["tag"] = tag
        if layer is not None:
            params["layer"] = layer
        set_active_val = coerce_bool(set_active)
        if set_active_val is not None:
            params["setActive"] = set_active_val
        if parent is not None:
            params["parent"] = parent
        if components_to_add is not None:
            params["componentsToAdd"] = components_to_add
        if components_to_remove is not None:
            params["componentsToRemove"] = components_to_remove
        if component_properties is not None:
            params["componentProperties"] = component_properties
        if create_child is not None:
            # Normalize vector fields within create_child (handles single object or array)
            def normalize_child_params(child: Any, index: int | None = None) -> tuple[dict | None, str | None]:
                prefix = f"create_child[{index}]" if index is not None else "create_child"
                if not isinstance(child, dict):
                    return None, f"{prefix} must be a dict with child properties (name, primitive_type, position, etc.), got {type(child).__name__}"
                child_params = dict(child)
                for vec_field in ("position", "rotation", "scale"):
                    if vec_field in child_params and child_params[vec_field] is not None:
                        vec_val, vec_err = normalize_vector3(child_params[vec_field], f"{prefix}.{vec_field}")
                        if vec_err:
                            return None, vec_err
                        child_params[vec_field] = vec_val
                return child_params, None

            if isinstance(create_child, list):
                # Array of children
                normalized_children = []
                for i, child in enumerate(create_child):
                    child_params, err = normalize_child_params(child, i)
                    if err:
                        return {"success": False, "message": err}
                    normalized_children.append(child_params)
                params["createChild"] = normalized_children
            else:
                # Single child object
                child_params, err = normalize_child_params(create_child)
                if err:
                    return {"success": False, "message": err}
                params["createChild"] = child_params

        if delete_child is not None:
            params["deleteChild"] = delete_child

        # Send command to Unity
        response = await send_with_unity_instance(
            async_send_command_with_retry, unity_instance, "manage_prefabs", params
        )

        # Return Unity response directly; ensure success field exists
        # Handle MCPResponse objects (returned on error) by converting to dict
        if hasattr(response, 'model_dump'):
            return response.model_dump()
        if isinstance(response, dict):
            if "success" not in response:
                response["success"] = False
            return response
        return {
            "success": False,
            "message": f"Unexpected response type: {type(response).__name__}"
        }

    except TimeoutError:
        return {
            "success": False,
            "message": "Unity connection timeout. Please check if Unity is running and responsive."
        }
    except Exception as exc:
        return {
            "success": False,
            "message": f"Error managing prefabs: {exc}"
        }
