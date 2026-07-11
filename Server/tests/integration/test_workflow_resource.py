"""Always-registered workflow resource tests."""

import asyncio
import importlib

from services.registry import get_registered_resources
from services.resources.workflow import get_workflow


def run(coro):
    return asyncio.run(coro)


def test_workflow_resource_is_always_registered():
    importlib.import_module("services.resources.workflow")

    resource = next(
        item for item in get_registered_resources() if item["name"] == "workflow"
    )
    assert resource["uri"] == "mcpforunity://workflow"
    assert "Read-only" in resource["description"]


def test_workflow_returns_the_five_stable_sections():
    result = run(get_workflow(object()))

    assert result["schema_version"] == 1
    assert set(result) == {"schema_version", "sections"}
    assert list(result["sections"]) == [
        "instance_routing",
        "safe_mutation",
        "script_compilation",
        "payload_sizing",
        "tool_groups",
    ]
    assert all(
        section["instructions"]
        for section in result["sections"].values()
    )


def test_server_instructions_only_point_to_on_demand_resources():
    from main import _build_instructions

    expected = (
        "Unity Editor automation server. Before first use, read "
        "mcpforunity://workflow and mcpforunity://capabilities."
    )
    assert _build_instructions(project_scoped_tools=True) == expected
    assert _build_instructions(project_scoped_tools=False) == expected
    assert len(expected) < 300
