"""Python-service dispatch for local REST CLI commands."""

from typing import Any


class CliServiceContext:
    """Minimal FastMCP context surface for local CLI service invocations."""

    def __init__(self, unity_instance: str | None):
        self._unity_instance = unity_instance

    async def get_state(self, key: str, default: Any = None) -> Any:
        if key == "unity_instance":
            return self._unity_instance
        return default

    async def info(self, _: str) -> None:
        return None


async def invoke_cli_service(
    command_type: str,
    params: dict[str, Any],
    unity_instance: str | None,
) -> dict[str, Any]:
    """Invoke the small set of CLI commands that require Python-side semantics."""
    if (command_type != "manage_editor"
            or params.get("action") != "set_scripting_defines"):
        raise ValueError(
            "Service invocation is only supported for manage_editor "
            "action=set_scripting_defines"
        )

    from services.tools.manage_editor import manage_editor

    return await manage_editor(
        CliServiceContext(unity_instance),
        action="set_scripting_defines",
        defines=params.get("defines"),
        target=params.get("target"),
    )
