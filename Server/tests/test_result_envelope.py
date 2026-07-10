import inspect

import pytest
from fastmcp.server.server import ToolResult
from mcp.types import ImageContent, TextContent

from services.tools.result_envelope import canonical_result, canonicalize_result


class _Context:
    async def get_state(self, key):
        assert key == "unity_instance"
        return "EnvelopeTests@abc123"


def test_canonicalize_legacy_response_preserves_extra_data():
    result = canonicalize_result(
        {"success": True, "message": "Done", "value": 42},
        unity_instance="Test@123",
        duration_ms=7,
    )

    assert result == {
        "success": True,
        "status": "completed",
        "message": "Done",
        "data": {"value": 42},
        "warnings": [],
        "error": None,
        "meta": {"unity_instance": "Test@123", "duration_ms": 7},
    }


def test_canonicalize_failure_supplies_error_and_normalizes_warnings():
    result = canonicalize_result(
        {"status": "error", "message": "Nope", "warnings": "Retry later"},
        unity_instance=None,
        duration_ms=0,
    )

    assert result["success"] is False
    assert result["status"] == "failed"
    assert result["error"] == "Nope"
    assert result["warnings"] == ["Retry later"]


def test_canonicalize_is_idempotent_for_existing_envelope():
    existing = canonicalize_result(
        {"success": True, "message": "Done", "data": {"value": 1}},
        unity_instance="First@123",
        duration_ms=2,
    )

    result = canonicalize_result(existing, unity_instance="Second@456", duration_ms=3)

    assert result["data"] == {"value": 1}
    assert result["status"] == "completed"
    assert result["meta"] == {"unity_instance": "Second@456", "duration_ms": 3}


@pytest.mark.asyncio
async def test_structured_is_default_and_does_not_duplicate_json_as_text():
    async def tool(ctx, value: int) -> dict:
        return {"success": True, "data": {"value": value}}

    wrapped = canonical_result(tool)
    result = await wrapped(_Context(), 3)

    assert result.content == []
    assert result.structured_content["data"] == {"value": 3}
    assert result.structured_content["meta"]["unity_instance"] == "EnvelopeTests@abc123"


@pytest.mark.asyncio
async def test_text_and_both_add_text_without_dropping_structured_content():
    async def tool(ctx) -> dict:
        return {"success": True, "message": "Done"}

    wrapped = canonical_result(tool)
    text_only = await wrapped(_Context(), response_format="text")
    both = await wrapped(_Context(), response_format="both", verbosity="detailed")

    assert text_only.structured_content["success"] is True
    assert len(text_only.content) == 1
    assert both.structured_content["success"] is True
    assert len(both.content) == 1
    assert "\n" in both.content[0].text


@pytest.mark.asyncio
async def test_content_bearing_result_retains_blocks_and_gets_structured_envelope():
    blocks = [
        TextContent(type="text", text='{"success":true,"message":"Captured","data":{"width":64}}'),
        ImageContent(type="image", data="aW1hZ2U=", mimeType="image/png"),
    ]

    async def tool(ctx) -> ToolResult:
        return ToolResult(content=blocks, meta={"trace_id": "abc"})

    result = await canonical_result(tool)(_Context())

    assert result.content == blocks
    assert result.meta == {"trace_id": "abc"}
    assert result.structured_content["success"] is True
    assert result.structured_content["message"] == "Captured"
    assert result.structured_content["data"] == {"width": 64}
    assert result.structured_content["meta"]["unity_instance"] == "EnvelopeTests@abc123"


def test_wrapper_advertises_standard_controls_and_tool_result_return():
    async def tool(ctx, required: str) -> dict:
        return {"success": True}

    signature = inspect.signature(canonical_result(tool))

    assert signature.parameters["required"].default is inspect.Parameter.empty
    assert signature.parameters["response_format"].default == "structured"
    assert signature.parameters["verbosity"].default == "compact"
