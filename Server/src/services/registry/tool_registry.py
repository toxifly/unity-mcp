"""
Tool registry for auto-discovery of MCP tools.

Tools can be assigned to *groups* via the ``group`` parameter.  Groups map to
FastMCP tags (``"group:<name>"``) which drive the per-session visibility
system exposed through the ``manage_tools`` meta-tool.

The special group value ``None`` means the tool is *always visible* and
cannot be disabled by the group system (used for server meta-tools like
``set_active_instance`` and ``manage_tools``).
"""
from typing import Callable, Any

# Global registry to collect decorated tools
_tool_registry: list[dict[str, Any]] = []

# Valid group names. ``None`` is also accepted (always-visible meta-tools).
TOOL_GROUPS: dict[str, str] = {
    "core": "Essential scene, script, asset & editor tools (always on by default)",
    "docs": "Unity API reflection and documentation lookup",
    "vfx": "Visual effects – VFX Graph, shaders, procedural textures",
    "animation": "Animator control & AnimationClip creation",
    "ui": "UI Toolkit (UXML, USS, UIDocument)",
    "scripting_ext": "ScriptableObject management",
    "testing": "Test runner & async test jobs",
    "probuilder": "ProBuilder 3D modeling – requires com.unity.probuilder package",
    "profiling": "Unity Profiler session control, counters, memory snapshots & Frame Debugger",
    "asset_gen": "AI asset generation – 3D model gen/import, 2D image gen & audio gen (bring-your-own-key)",
}

# ``testing`` ships enabled because it is only two tools and an agent that cannot see
# them cannot discover them either: a hidden tool is absent from ``tools/list``, so the
# usual "search for run_tests" reflex returns nothing and the group has to be activated
# from prior knowledge before the first test can ever be run.
DEFAULT_ENABLED_GROUPS: set[str] = {"core", "testing"}


def mcp_for_unity_tool(
    name: str | None = None,
    description: str | None = None,
    unity_target: str | None = "self",
    group: str | None = "core",
    **kwargs
) -> Callable:
    """
    Decorator for registering MCP tools in the server's tools directory.

    Tools are registered in the global tool registry.

    Args:
        name: Tool name (defaults to function name)
        description: Tool description
        unity_target: Visibility target used by middleware filtering.
            - "self" (default): tool follows its own enabled state.
            - None: server-only tool, always visible in tool listing.
            - "<tool_name>": alias tool that follows another Unity tool state.
        group: Tool group for dynamic visibility.
            - A group name string (e.g. "core", "vfx") assigns the tool to
              that group and adds a ``tags={"group:<name>"}`` entry.
            - None: the tool is *always visible* (server meta-tools).
        **kwargs: Additional arguments passed to @mcp.tool()

    Example:
        @mcp_for_unity_tool(description="Does something cool")
        async def my_custom_tool(ctx: Context, ...):
            pass
    """
    def decorator(func: Callable) -> Callable:
        tool_name = name if name is not None else func.__name__
        # Safety guard: unity_target is internal metadata and must never leak into mcp.tool kwargs.
        tool_kwargs = dict(kwargs)  # Create a copy to avoid side effects
        if "unity_target" in tool_kwargs:
            del tool_kwargs["unity_target"]
        if "group" in tool_kwargs:
            del tool_kwargs["group"]

        # Validate and normalize group
        resolved_group: str | None = None
        if group is not None:
            if group not in TOOL_GROUPS:
                raise ValueError(
                    f"Unknown group '{group}' for tool '{tool_name}'. "
                    f"Valid groups: {', '.join(sorted(TOOL_GROUPS))}."
                )
            resolved_group = group
            # Merge the group tag into any existing tags the caller provided
            existing_tags: set[str] = set(tool_kwargs.get("tags") or set())
            existing_tags.add(f"group:{group}")
            tool_kwargs["tags"] = existing_tags

        if unity_target is None:
            normalized_unity_target: str | None = None
        elif isinstance(unity_target, str) and unity_target.strip():
            normalized_unity_target = (
                tool_name if unity_target == "self" else unity_target.strip()
            )
        else:
            raise ValueError(
                f"Invalid unity_target for tool '{tool_name}': {unity_target!r}. "
                "Expected None or a non-empty string."
            )

        _tool_registry.append({
            'func': func,
            'name': tool_name,
            'description': description,
            'unity_target': normalized_unity_target,
            'group': resolved_group,
            'kwargs': tool_kwargs,
        })

        return func

    return decorator


def get_registered_tools() -> list[dict[str, Any]]:
    """Get all registered tools"""
    return _tool_registry.copy()


def get_group_tool_names() -> dict[str, list[str]]:
    """Return a mapping of group name -> list of tool names in that group."""
    result: dict[str, list[str]] = {g: [] for g in TOOL_GROUPS}
    for tool in _tool_registry:
        g = tool.get("group")
        if g and g in result:
            result[g].append(tool["name"])
    return result


def parse_session_group_overrides(rules: list | None) -> dict[str, bool]:
    """Extract per-group enabled overrides from FastMCP session visibility rules.

    Rules accumulate; the last rule whose tags include ``group:<name>`` wins.
    """
    overrides: dict[str, bool] = {}
    for rule in rules or []:
        tags = rule.get("tags") or []
        enabled = rule.get("enabled", True)
        for tag in tags:
            if isinstance(tag, str) and tag.startswith("group:"):
                overrides[tag[len("group:"):]] = enabled
    return overrides


def get_group_visibility_states(
    session_overrides: dict[str, bool] | None = None,
) -> list[dict[str, Any]]:
    """Per-group visibility combining declared defaults, the last Unity sync
    (server level), and per-session overrides.

    Precedence mirrors FastMCP evaluation order: session rules override the
    server-level Unity sync, which overrides the declared defaults.
    """
    try:
        from transport.plugin_hub import PluginHub
        unity_sync = PluginHub.get_last_unity_sync()
    except Exception:
        unity_sync = None
    unity_state: dict[str, bool] = (unity_sync or {}).get("group_state") or {}

    group_tools = get_group_tool_names()
    session_overrides = session_overrides or {}
    groups: list[dict[str, Any]] = []
    for name in sorted(TOOL_GROUPS):
        default_enabled = name in DEFAULT_ENABLED_GROUPS
        if name in session_overrides:
            enabled, source = session_overrides[name], "session"
        elif name in unity_state:
            enabled, source = unity_state[name], "unity"
        else:
            enabled, source = default_enabled, "default"
        groups.append({
            "name": name,
            "description": TOOL_GROUPS[name],
            "enabled": enabled,
            "source": source,
            "default_enabled": default_enabled,
            "unity_persisted": unity_state.get(name),
            "tools": group_tools.get(name, []),
            "tool_count": len(group_tools.get(name, [])),
        })
    return groups


def clear_tool_registry():
    """Clear the tool registry (useful for testing)"""
    _tool_registry.clear()
