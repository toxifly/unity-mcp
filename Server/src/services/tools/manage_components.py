"""
Tool for managing components on GameObjects in Unity.
Supports add, remove, set_property, get_properties, and get_components operations.
"""
from typing import Annotated, Any, Literal, Optional

from fastmcp import Context
from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry
from services.tools.utils import parse_json_payload, normalize_properties, coerce_bool, coerce_int, filter_component_properties
from services.tools.preflight import preflight


@mcp_for_unity_tool(
    description=(
        "Manage components on existing GameObjects. Read-only actions: get_properties and "
        "get_components. Mutating actions: add, remove, and set_property. Targets accept a "
        "GameObject name, hierarchy path, instance ID, or structured target; property filters "
        "can bound serialized-property reads."
    )
)
async def manage_components(
    ctx: Context,
    action: Annotated[
        Literal["add", "remove", "set_property", "get_properties", "get_components"],
        "Action to perform: add, remove, set_property, get_properties, get_components"
    ],
    target: Annotated[
        dict[str, Any] | str | int,
        "Target GameObject reference - instance ID, name, path, or object like {\"instanceID\": 123} / {\"name\": \"Player\"} / {\"path\": \"/Canvas/Panel\"}"
    ],
    component_type: Annotated[
        str,
        "Component type name (e.g., 'Rigidbody', 'BoxCollider', 'MyScript')"
    ] | None = None,
    search_method: Annotated[
        Optional[Literal["by_id", "by_name", "by_path"]],
        "How to find the target GameObject"
    ] = None,
    # For set_property action - single property
    property: Annotated[Optional[str],
                        "Property name to set (for set_property action)"] = None,
    value: Annotated[Optional[str | int | float | bool | dict | list],
                     "Value to set (for set_property action). "
                     "For object references: instance ID (int), asset path (string), "
                     "or {\"guid\": \"...\"} / {\"path\": \"...\"}. "
                     "For Sprite sub-assets: {\"guid\": \"...\", \"spriteName\": \"<name>\"} or "
                     "{\"guid\": \"...\", \"fileID\": <id>}. Single-sprite textures auto-resolve."] = None,
    # For add/set_property - multiple properties
    properties: Annotated[
        Optional[dict[str, Any] | str],
        "Dictionary of property names to values. Example: {\"mass\": 5.0, \"useGravity\": false}"
    ] | None = None,
    # For targeting a specific component when multiple of the same type exist
    component_index: Annotated[
        Optional[int],
        "Zero-based index to select which component when multiple of the same type exist. "
        "Use the components resource to discover indices. If omitted, targets the first instance."
    ] = None,
    # For get_properties action
    property_names: Annotated[
        list[str] | str,
        "For get_properties: list of property/field names to read. Accepts a JSON array string or a comma-separated string."
    ] | None = None,
    # For get_components action
    page_size: Annotated[int | str, "For get_components: page size (default 25)."] | None = None,
    cursor: Annotated[int | str, "For get_components: pagination cursor (default 0)."] | None = None,
    include_properties: Annotated[bool | str, "For get_components: include serialized component properties (default false)."] | None = None,
    property_whitelist: Annotated[list[str], "For get_components: only return these property names (requires include_properties=true)."] | None = None,
    property_blacklist: Annotated[list[str], "For get_components: exclude these property names from results."] | None = None,
    change_guard: Annotated[
        dict[str, Any] | str,
        "Reject and roll back mutations outside expected_objects/expected_properties or max_changed_objects."
    ] | None = None,
    dry_run: Annotated[
        bool | str,
        "Preview the exact serialized changes, then roll them back without saving or changing dirty state."
    ] | None = None,
) -> dict[str, Any]:
    """
    Manage components on GameObjects.

    Actions:
    - add: Add a new component to a GameObject
    - remove: Remove a component from a GameObject  
    - set_property: Set one or more properties on a component

    Examples:
    - Add Rigidbody: action="add", target="Player", component_type="Rigidbody"
    - Remove BoxCollider: action="remove", target=-12345, component_type="BoxCollider"
    - Set single property: action="set_property", target="Enemy", component_type="Rigidbody", property="mass", value=5.0
    - Set multiple properties: action="set_property", target="Enemy", component_type="Rigidbody", properties={"mass": 5.0, "useGravity": false}
    - Get specific properties: action="get_properties", target={"path":"/Canvas/Panel"}, component_type="RectTransform", property_names=["anchoredPosition","sizeDelta"]
    - Get all components with values: action="get_components", target={"name":"Player"}, page_size=25, cursor=0, include_properties=true
    """
    unity_instance = await get_unity_instance_from_context(ctx)

    gate = await preflight(ctx, wait_for_no_compile=True, refresh_if_dirty=True)
    if gate is not None:
        return gate.model_dump()

    if not action:
        return {
            "success": False,
            "message": "Missing required parameter 'action'. Valid actions: add, remove, set_property"
        }

    if not target:
        return {
            "success": False,
            "message": "Missing required parameter 'target'. Specify GameObject instance ID or name."
        }

    if action in {"add", "remove", "set_property", "get_properties"} and not component_type:
        return {
            "success": False,
            "message": f"Missing required parameter 'component_type' for action '{action}'. Specify the component type name."
        }

    # --- Normalize properties with detailed error handling ---
    properties, props_error = normalize_properties(properties)
    if props_error:
        return {"success": False, "message": props_error}

    change_guard = parse_json_payload(change_guard)
    if change_guard is not None and not isinstance(change_guard, dict):
        return {"success": False, "code": "INVALID_CHANGE_GUARD", "message": "change_guard must be a JSON object."}
    dry_run = coerce_bool(dry_run, default=False)

    # --- Validate value parameter for serialization issues ---
    if value is not None and isinstance(value, str) and value in ("[object Object]", "undefined"):
        return {"success": False, "message": f"value received invalid input: '{value}'. Expected an actual value."}

    def _normalize_target_and_method(
        raw_target: Any, raw_search_method: str | None
    ) -> tuple[Any, str | None, str | None]:
        """Normalize {instanceID|name|path} references into (target, searchMethod)."""
        # Accept JSON-stringified objects (some MCP clients stringify nested objects)
        raw_target = parse_json_payload(raw_target)

        if isinstance(raw_target, dict):
            instance_id = raw_target.get("instanceID") or raw_target.get("instance_id") or raw_target.get("id")
            if instance_id is not None:
                coerced = coerce_int(instance_id, default=None)
                if coerced is None:
                    return None, None, f"Invalid instanceID in target: {instance_id!r}"
                return coerced, "by_id", None

            path = raw_target.get("path")
            if isinstance(path, str) and path.strip():
                p = path.strip()
                if p.startswith("/"):
                    p = p[1:]
                return p, "by_path", None

            name = raw_target.get("name")
            if isinstance(name, str) and name.strip():
                return name.strip(), "by_name", None

            return None, None, "Invalid target object: expected one of {instanceID,name,path}."

        # Primitive targets: keep as-is and infer method if not explicitly provided.
        if raw_search_method:
            return raw_target, raw_search_method, None

        if isinstance(raw_target, int):
            return raw_target, "by_id", None

        if isinstance(raw_target, str):
            s = raw_target.strip()
            if s.startswith("/"):
                return s[1:], "by_path", None
            if "/" in s:
                return s, "by_path", None
            return s, "by_name", None

        return raw_target, None, None

    async def _resolve_instance_id(
        normalized_target: Any,
        method: str | None,
    ) -> tuple[int | None, dict[str, Any] | None]:
        """Resolve a target reference to a concrete GameObject instanceID."""
        if isinstance(normalized_target, int):
            return normalized_target, None

        if method == "by_id":
            coerced = coerce_int(normalized_target, default=None)
            if coerced is None:
                return None, {"success": False, "message": f"Invalid instance ID target: {normalized_target!r}"}
            return coerced, None

        if not isinstance(normalized_target, str) or not normalized_target.strip():
            return None, {"success": False, "message": "Unable to resolve target to a GameObject instance ID."}

        search_term = normalized_target.strip()
        search_method = method or ("by_path" if "/" in search_term else "by_name")

        # Ask Unity to resolve to an instance ID
        find_params = {
            "searchMethod": search_method,
            "searchTerm": search_term,
            "page_size": 2,  # detect ambiguity cheaply
            "cursor": 0,
            "includeInactive": True,
        }
        find_resp = await send_with_unity_instance(
            async_send_command_with_retry,
            unity_instance,
            "find_gameobjects",
            find_params,
        )
        if not isinstance(find_resp, dict) or not find_resp.get("success"):
            return None, find_resp if isinstance(find_resp, dict) else {"success": False, "message": str(find_resp)}

        data = find_resp.get("data") if isinstance(find_resp.get("data"), dict) else {}
        ids = data.get("instanceIDs") or data.get("instanceIds") or []
        if not isinstance(ids, list) or len(ids) == 0:
            return None, {"success": False, "message": f"Target not found (method={search_method} term={search_term!r})."}
        if search_method == "by_name" and len(ids) > 1:
            return None, {
                "success": False,
                "message": f"Target name {search_term!r} is ambiguous ({len(ids)} matches). Use a path (by_path) or instanceID.",
                "data": {"instanceIDs": ids},
            }
        coerced_id = coerce_int(ids[0], default=None)
        if coerced_id is None:
            return None, {"success": False, "message": f"Unity returned invalid instanceID: {ids[0]!r}"}
        return coerced_id, None

    try:
        normalized_target, inferred_method, target_error = _normalize_target_and_method(target, search_method)
        if target_error:
            return {"success": False, "message": target_error}

        effective_method = search_method or inferred_method

        # Read actions (implemented via existing Unity resources)
        if action == "get_components":
            instance_id, err = await _resolve_instance_id(normalized_target, effective_method)
            if err:
                return err

            page_size_val = coerce_int(page_size, default=25) or 25
            cursor_val = coerce_int(cursor, default=0) or 0
            include_props_val = coerce_bool(include_properties, default=False)
            if include_props_val is None:
                include_props_val = False

            response = await send_with_unity_instance(
                async_send_command_with_retry,
                unity_instance,
                "get_gameobject_components",
                {
                    "instanceID": instance_id,
                    "pageSize": page_size_val,
                    "cursor": cursor_val,
                    "includeProperties": include_props_val,
                },
            )

            if isinstance(response, dict) and response.get("success") and (property_whitelist or property_blacklist):
                data = response.get("data")
                if isinstance(data, dict):
                    components = data.get("components")
                    if isinstance(components, list):
                        components, was_filtered = filter_component_properties(
                            components, property_whitelist, property_blacklist,
                        )
                        data["components"] = components
                        if was_filtered:
                            data["_filtered"] = True
                            parts = []
                            if property_whitelist:
                                parts.append(f"whitelist={property_whitelist}")
                            if property_blacklist:
                                parts.append(f"blacklist={property_blacklist}")
                            data["_filter"] = ", ".join(parts)

            return response if isinstance(response, dict) else {"success": False, "message": str(response)}

        if action == "get_properties":
            instance_id, err = await _resolve_instance_id(normalized_target, effective_method)
            if err:
                return err

            response = await send_with_unity_instance(
                async_send_command_with_retry,
                unity_instance,
                "get_gameobject_component",
                {
                    "instanceID": instance_id,
                    "componentName": component_type,
                },
            )
            if not isinstance(response, dict) or not response.get("success"):
                return response if isinstance(response, dict) else {"success": False, "message": str(response)}

            # If no specific properties requested, return the full component payload.
            if property is None and property_names is None:
                return response

            # Normalize property_names input
            requested: list[str] = []
            if property:
                requested.append(property)
            if property_names is not None:
                pn = parse_json_payload(property_names)
                if isinstance(pn, list):
                    requested.extend([str(x) for x in pn if str(x).strip()])
                elif isinstance(pn, str):
                    parts = [p.strip() for p in pn.split(",")]
                    requested.extend([p for p in parts if p])
                else:
                    return {"success": False, "message": "property_names must be a list or string."}

            # De-dupe while preserving order
            seen = set()
            requested = [p for p in requested if not (p in seen or seen.add(p))]

            data = response.get("data") if isinstance(response.get("data"), dict) else {}
            component = data.get("component") if isinstance(data.get("component"), dict) else {}

            def _try_get_nested(d: Any, key_path: str) -> tuple[bool, Any]:
                cur = d
                for part in key_path.split("."):
                    if isinstance(cur, dict) and part in cur:
                        cur = cur[part]
                    else:
                        return False, None
                return True, cur

            values: dict[str, Any] = {}
            missing: list[str] = []
            for key in requested:
                exists, v = _try_get_nested(component, key)
                if not exists:
                    missing.append(key)
                else:
                    values[key] = v

            return {
                "success": True,
                "message": f"Read {len(values)} properties from component '{component_type}'.",
                "data": {
                    "instanceID": instance_id,
                    "componentType": component_type,
                    "properties": values,
                    "missing": missing,
                },
            }

        # Mutating actions (passthrough to Unity tool)
        params = {
            "action": action,
            "target": normalized_target,
            "componentType": component_type,
        }

        if effective_method:
            params["searchMethod"] = effective_method

        if component_index is not None:
            params["componentIndex"] = component_index

        if action == "set_property":
            if property and value is not None:
                params["property"] = property
                params["value"] = value
            if properties:
                params["properties"] = properties

        if action == "add" and properties:
            params["properties"] = properties

        if change_guard is not None:
            params["changeGuard"] = change_guard
        if dry_run:
            params["dryRun"] = True

        response = await send_with_unity_instance(
            async_send_command_with_retry,
            unity_instance,
            "manage_components",
            params,
        )

        if isinstance(response, dict) and response.get("success"):
            return {
                "success": True,
                "message": response.get("message", f"Component {action} successful."),
                "data": response.get("data")
            }
        return response if isinstance(response, dict) else {"success": False, "message": str(response)}

    except Exception as e:
        return {"success": False, "message": f"Error managing component: {e!s}"}
