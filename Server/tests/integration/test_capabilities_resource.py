"""Capability discovery resource tests."""

import asyncio
import importlib

from services.registry import get_registered_resources, get_registered_tools
from services.resources.capabilities import get_capabilities

# The production server discovers tools before resources. Import the backing tools
# explicitly so this focused module has the same registry ordering in isolation.
import services.tools.inspect_provenance  # noqa: F401,E402
import services.tools.inspect_serialized  # noqa: F401,E402
import services.tools.measure_ui  # noqa: F401,E402
import services.tools.manage_components  # noqa: F401,E402
import services.tools.manage_gameobject  # noqa: F401,E402


def run(coro):
    return asyncio.run(coro)


def test_capabilities_resource_is_registered():
    importlib.import_module("services.resources.capabilities")

    resource = next(
        item for item in get_registered_resources() if item["name"] == "capabilities"
    )
    assert resource["uri"] == "mcpforunity://capabilities"


def test_capabilities_manifest_is_compact_and_matches_registered_tools():
    result = run(get_capabilities(object()))

    assert result["schema_version"] == 1
    assert result["tools"] == {
        "measure_ui": {"version": 1},
        "inspect_serialized": {"version": 1},
        "inspect_provenance": {"version": 1},
        "mutation_transactions": {"version": 1},
    }
    assert set(result) == {"schema_version", "tools"}


def test_capabilities_omit_tools_that_are_not_registered(monkeypatch):
    registered = get_registered_tools()
    without_measure_ui = [
        tool for tool in registered if tool["name"] != "measure_ui"
    ]
    monkeypatch.setattr(
        "services.resources.capabilities.get_registered_tools",
        lambda: without_measure_ui,
    )

    result = run(get_capabilities(object()))

    assert "measure_ui" not in result["tools"]
    assert "inspect_serialized" in result["tools"]
