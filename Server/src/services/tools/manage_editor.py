from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from core.telemetry import is_telemetry_enabled, record_tool_usage
from services.tools import get_unity_instance_from_context
from services.tools.refresh_unity import send_mutation
from services.tools.utils import parse_json_payload
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry

@mcp_for_unity_tool(
    description="Control and query Unity Editor state and settings. Read-only actions: telemetry_status, telemetry_ping, and get_scripting_defines. Mutating actions: play, pause, stop, set_active_tool, add_tag, remove_tag, add_layer, remove_layer, set_scripting_defines, deploy_package, restore_package, undo, and redo. set_scripting_defines replaces the whole symbol list for a build target (pass [] to clear) and triggers a recompile. deploy_package copies the configured MCPForUnity source into the installed package and triggers recompilation without a confirmation dialog; restore_package restores its backup. undo and redo return the affected group name.",
    annotations=ToolAnnotations(
        title="Manage Editor",
    ),
)
async def manage_editor(
    ctx: Context,
    action: Annotated[Literal["telemetry_status", "telemetry_ping", "play", "pause", "stop", "set_active_tool", "add_tag", "remove_tag", "add_layer", "remove_layer", "get_scripting_defines", "set_scripting_defines", "deploy_package", "restore_package", "undo", "redo"], "Editor action. deploy_package copies the configured MCPForUnity source into the project's package location and triggers recompilation; restore_package restores its backup; undo and redo apply Editor undo groups."],
    tool_name: Annotated[str,
                         "Tool name when setting active tool"] | None = None,
    tag_name: Annotated[str,
                        "Tag name when adding and removing tags"] | None = None,
    layer_name: Annotated[str,
                          "Layer name when adding and removing layers"] | None = None,
    defines: Annotated[list[str] | str,
                       "Full scripting define symbol list for set_scripting_defines; [] clears them"] | None = None,
    target: Annotated[str,
                      "Build target for scripting defines (e.g. windows64, android); defaults to the active one"] | None = None,
) -> dict[str, Any]:
    # Get active instance from request state (injected by middleware)
    unity_instance = await get_unity_instance_from_context(ctx)

    try:
        # Diagnostics: quick telemetry checks
        if action == "telemetry_status":
            return {"success": True, "telemetry_enabled": is_telemetry_enabled()}

        if action == "telemetry_ping":
            record_tool_usage("diagnostic_ping", True, 1.0, None)
            return {"success": True, "message": "telemetry ping queued"}

        if action == "set_scripting_defines" and defines is None:
            return {"success": False, "message": "defines is required for set_scripting_defines (pass [] to clear)"}

        # A JSON-array string is the common client serialization; anything else (a plain
        # "A;B" string, a real list) is normalized by the C# handler, which the CLI shares.
        if isinstance(defines, str):
            parsed = parse_json_payload(defines)
            if isinstance(parsed, list):
                defines = parsed

        # Prepare parameters, removing None values
        params = {
            "action": action,
            "toolName": tool_name,
            "tagName": tag_name,
            "layerName": layer_name,
            "defines": defines,
            "target": target,
        }
        params = {k: v for k, v in params.items() if v is not None}

        if action == "set_scripting_defines":
            # The write recompiles and reloads the domain, which can kill the connection
            # before the reply lands. Recover the way script mutations do, and verify by
            # reading the symbols back rather than reporting a false failure.
            async def verify_after_disconnect() -> dict[str, Any] | None:
                # Writing the same symbols is a no-op (changed=false), so a plain re-send both
                # confirms the first write landed and completes it if it did not.
                retry = await send_with_unity_instance(
                    async_send_command_with_retry, unity_instance, "manage_editor", params)
                return retry if isinstance(retry, dict) and retry.get("success") else None

            response = await send_mutation(
                ctx, unity_instance, "manage_editor", params,
                verify_after_disconnect=verify_after_disconnect,
            )
        else:
            # Send command using centralized retry helper with instance routing
            response = await send_with_unity_instance(async_send_command_with_retry, unity_instance, "manage_editor", params)

        # Preserve structured failure data; unwrap success into a friendlier shape
        if isinstance(response, dict) and response.get("success"):
            return {"success": True, "message": response.get("message", "Editor operation successful."), "data": response.get("data")}
        return response if isinstance(response, dict) else {"success": False, "message": str(response)}

    except Exception as e:
        return {"success": False, "message": f"Python error managing editor: {str(e)}"}
