from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry
from services.tools.utils import coerce_bool, coerce_int, parse_json_payload


@mcp_for_unity_tool(
    description="Performs prefab operations (open_stage, close_stage, save_open_stage, create_from_gameobject, apply_instance_overrides, revert_instance_overrides, unpack_instance).",
    annotations=ToolAnnotations(
        title="Manage Prefabs",
        destructiveHint=True,
    ),
)
async def manage_prefabs(
    ctx: Context,
    action: Annotated[
        Literal[
            "open_stage",
            "close_stage",
            "save_open_stage",
            "create_from_gameobject",
            "apply_instance_overrides",
            "revert_instance_overrides",
            "unpack_instance",
        ],
        "Perform prefab operations.",
    ],
    prefab_path: Annotated[str,
                           "Prefab asset path relative to Assets e.g. Assets/Prefabs/favorite.prefab"] | None = None,
    mode: Annotated[str,
                    "Optional prefab stage mode (only 'InIsolation' is currently supported)"] | None = None,
    save_before_close: Annotated[bool | str,
                                 "When true, `close_stage` will save the prefab before exiting the stage."] | None = None,
    target: Annotated[dict[str, Any] | str | int,
                      "Scene GameObject reference for create_from_gameobject. Accepts instance ID, name, path, or object like {\"instanceID\": 123} / {\"name\": \"Player\"} / {\"path\": \"/Canvas/Panel\"}."] | None = None,
    search_method: Annotated[Literal["by_id", "by_name", "by_path"],
                             "How to resolve the target for create_from_gameobject (optional)."] | None = None,
    allow_overwrite: Annotated[bool | str,
                               "Allow replacing an existing prefab at the same path"] | None = None,
    search_inactive: Annotated[bool | str,
                               "Include inactive objects when resolving the target"] | None = None,
    unpack_mode: Annotated[str,
                           "For unpack_instance: unpack mode. Valid values: OutermostRoot, Completely."] | None = None,
) -> dict[str, Any]:
    # Get active instance from session state
    # Removed session_state import
    unity_instance = get_unity_instance_from_context(ctx)

    try:
        params: dict[str, Any] = {"action": action}

        if prefab_path:
            params["prefabPath"] = prefab_path
        if mode:
            params["mode"] = mode
        save_before_close_val = coerce_bool(save_before_close)
        if save_before_close_val is not None:
            params["saveBeforeClose"] = save_before_close_val

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
        elif action in {"create_from_gameobject", "apply_instance_overrides", "revert_instance_overrides", "unpack_instance"}:
            return {"success": False, "message": f"'target' is required for action '{action}'."}

        allow_overwrite_val = coerce_bool(allow_overwrite)
        if allow_overwrite_val is not None:
            params["allowOverwrite"] = allow_overwrite_val
        search_inactive_val = coerce_bool(search_inactive)
        if search_inactive_val is not None:
            params["searchInactive"] = search_inactive_val

        if unpack_mode and action == "unpack_instance":
            params["unpackMode"] = unpack_mode
        response = await send_with_unity_instance(async_send_command_with_retry, unity_instance, "manage_prefabs", params)

        if isinstance(response, dict) and response.get("success"):
            return {
                "success": True,
                "message": response.get("message", "Prefab operation successful."),
                "data": response.get("data"),
            }
        return response if isinstance(response, dict) else {"success": False, "message": str(response)}
    except Exception as exc:
        return {"success": False, "message": f"Python error managing prefabs: {exc}"}
