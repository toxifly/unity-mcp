"""Registration-order-independent catalog of the server's bridge capabilities.

The bridge handshake must advertise the same resources and tool groups no
matter which entrypoint builds it — the full FastMCP server, a stdio port
probe, or the raw bridge smoke test. Importing the tool/resource modules
populates the decorator registries deterministically, so the catalog forces
that discovery itself and reads the registries directly, never FastMCP
construction state (which transport-only callers do not have).
"""

from pathlib import Path

from services.registry import get_registered_resources, get_registered_tools
from utils.module_discovery import discover_modules

_discovered = False


def _ensure_catalog_discovered() -> None:
    global _discovered
    if _discovered:
        return
    # Imported lazily so transport-only modules can import this one cheaply.
    import services.resources as resources_package
    import services.tools as tools_package

    list(discover_modules(
        Path(resources_package.__file__).parent, resources_package.__name__))
    list(discover_modules(
        Path(tools_package.__file__).parent, tools_package.__name__))
    _discovered = True


def get_catalog_resource_uris() -> list[str]:
    """Every resource URI this build ships, independent of server configuration."""
    _ensure_catalog_discovered()
    return sorted({str(info["uri"]) for info in get_registered_resources()})


def get_catalog_tool_groups() -> list[str]:
    """Every tool group this build ships, independent of server configuration."""
    _ensure_catalog_discovered()
    return sorted({
        str(tool["group"])
        for tool in get_registered_tools()
        if tool.get("group")
    })
