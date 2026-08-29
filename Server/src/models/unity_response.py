"""Utilities for normalizing Unity transport responses."""
from __future__ import annotations

from typing import Any, Type

from models.models import MCPResponse


def _preserve_queue_metadata(
    response: dict[str, Any], normalized: dict[str, Any]
) -> dict[str, Any]:
    """Carry transport queue diagnostics onto the unwrapped response.

    Unity reports these diagnostics beside ``result``. The legacy transport
    exposes a non-empty value as ``result["queue"]``, without replacing queue
    metadata already supplied by the result itself. Keep that contract when
    normalizing PluginHub responses as well.
    """
    queue = response.get("queue")
    if not queue or "queue" in normalized:
        return normalized
    return {**normalized, "queue": queue}


def normalize_unity_response(response: Any) -> Any:
    """Normalize Unity's {status,result} payloads into MCPResponse shape."""
    if not isinstance(response, dict):
        return response

    status = response.get("status")
    result = response.get("result") if isinstance(
        response.get("result"), dict) else response.get("result")

    # Already MCPResponse-shaped
    if "success" in response:
        return response
    if isinstance(result, dict) and "success" in result:
        return _preserve_queue_metadata(response, result)

    if status is None:
        return response

    payload = result if isinstance(result, dict) else {}
    success = status == "success"
    message = payload.get("message") or response.get("message")
    error = payload.get("error") or response.get("error")

    data = payload.get("data")
    if data is None and isinstance(payload, dict) and payload:
        data = {k: v for k, v in payload.items() if k not in {
            "message", "error", "status", "code"}}
        if not data:
            data = None

    normalized: dict[str, Any] = {
        "success": success,
        "message": message,
        "error": error if not success else None,
        "data": data,
    }

    if not success and not normalized["error"]:
        normalized["error"] = message or "Unity command failed"

    return _preserve_queue_metadata(response, normalized)


def parse_resource_response(response: Any, typed_cls: Type[MCPResponse]) -> MCPResponse:
    """Parse a Unity response into a typed response class.

    Returns a base ``MCPResponse`` for error responses so that typed subclasses
    with strict ``data`` fields (e.g. ``list[str]``) don't raise Pydantic
    validation errors when ``data`` is ``None``.
    """
    if not isinstance(response, dict):
        return response

    # Detect errors from both normalized (success=False) and raw (status="error") shapes.
    if response.get("success") is False or response.get("status") == "error":
        return MCPResponse(
            success=False,
            error=response.get("error"),
            message=response.get("message"),
            queue=response.get("queue"),
        )

    return typed_cls(**response)
