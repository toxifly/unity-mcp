"""Canonical MCP tool result envelopes and response rendering."""

from __future__ import annotations

import functools
import inspect
import json
import time
from collections.abc import Callable
from typing import Annotated, Any, Literal

from fastmcp.server.server import ToolResult
from mcp.types import TextContent
from pydantic import Field


ResponseFormat = Literal["structured", "text", "both"]
Verbosity = Literal["compact", "normal", "detailed"]

RESPONSE_FORMAT_ANNOTATION = Annotated[
    ResponseFormat,
    Field(
        description=(
            "Result representation. The canonical structured result is always returned. "
            "'structured' (default) omits text content; 'text' and 'both' also add JSON text."
        )
    ),
]
VERBOSITY_ANNOTATION = Annotated[
    Verbosity,
    Field(description="Response detail level. Compact is the token-efficient default."),
]

CANONICAL_RESULT_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "success": {"type": "boolean"},
        "status": {"type": "string"},
        "message": {"type": ["string", "null"]},
        "data": {},
        "warnings": {"type": "array", "items": {}},
        "error": {},
        "meta": {
            "type": "object",
            "properties": {
                "unity_instance": {"type": ["string", "null"]},
                "duration_ms": {"type": "integer", "minimum": 0},
            },
            "required": ["unity_instance", "duration_ms"],
            "additionalProperties": True,
        },
    },
    "required": [
        "success",
        "status",
        "message",
        "data",
        "warnings",
        "error",
        "meta",
    ],
    "additionalProperties": False,
}

_ENVELOPE_KEYS = {"success", "status", "message", "data", "warnings", "error", "meta"}
_TRANSPORT_STATUS = {"success", "error", "ok", "failed", "failure"}


def _success_from_payload(payload: dict[str, Any]) -> bool:
    if "success" in payload:
        return bool(payload["success"])
    status = str(payload.get("status", "success")).lower()
    return status not in {"error", "failed", "failure"}


def _normalize_warnings(value: Any) -> list[Any]:
    if value is None:
        return []
    return value if isinstance(value, list) else [value]


def canonicalize_result(
    result: Any,
    *,
    unity_instance: str | None,
    duration_ms: int,
) -> dict[str, Any]:
    """Convert a legacy/raw tool return value to the canonical result shape."""
    if hasattr(result, "model_dump"):
        result = result.model_dump(mode="json")

    payload = result if isinstance(result, dict) else {"data": result}

    # A ToolResult may already carry the canonical representation. Keep this
    # conversion idempotent while refreshing per-call metadata.
    if _ENVELOPE_KEYS.issubset(payload):
        meta = payload.get("meta") if isinstance(payload.get("meta"), dict) else {}
        return {
            "success": bool(payload["success"]),
            "status": str(payload["status"]),
            "message": None if payload["message"] is None else str(payload["message"]),
            "data": payload["data"],
            "warnings": _normalize_warnings(payload["warnings"]),
            "error": payload["error"],
            "meta": {
                **meta,
                "unity_instance": unity_instance,
                "duration_ms": max(0, int(duration_ms)),
            },
        }

    # Unwrap the legacy Unity transport shape without leaking transport status into
    # the operation-level status field.
    transport_status = str(payload.get("status", "")).lower()
    if transport_status in _TRANSPORT_STATUS and "result" in payload:
        nested = payload.get("result")
        if isinstance(nested, dict):
            payload = {**nested, **{k: v for k, v in payload.items() if k != "result"}}
        else:
            payload = {**payload, "data": nested}

    success = _success_from_payload(payload)
    message = payload.get("message")
    if message is not None:
        message = str(message)
        if len(message) > 160:
            message = f"{message[:157]}..."

    error = payload.get("error")
    if not success and error is None:
        error = message or "Tool operation failed"

    explicit_data = payload.get("data")
    extras = {
        key: value
        for key, value in payload.items()
        if key not in _ENVELOPE_KEYS and key != "result"
    }
    # Preserve non-transport statuses (for example a returned job's "running"
    # state) as data while keeping the envelope's operation status canonical.
    if "status" in payload and transport_status not in _TRANSPORT_STATUS:
        extras["status"] = payload["status"]

    if explicit_data is None:
        data: Any = extras or None
    elif extras and isinstance(explicit_data, dict):
        data = {**explicit_data, **extras}
    elif extras:
        data = {"value": explicit_data, **extras}
    else:
        data = explicit_data

    meta = payload.get("meta") if isinstance(payload.get("meta"), dict) else {}
    meta = {
        **meta,
        "unity_instance": unity_instance,
        "duration_ms": max(0, int(duration_ms)),
    }

    return {
        "success": success,
        "status": "completed" if success else "failed",
        "message": message,
        "data": data,
        "warnings": _normalize_warnings(payload.get("warnings")),
        "error": None if success else error,
        "meta": meta,
    }


async def _unity_instance_from_call(args: tuple[Any, ...], kwargs: dict[str, Any]) -> str | None:
    ctx = args[0] if args else kwargs.get("ctx")
    get_state = getattr(ctx, "get_state", None)
    if get_state is None:
        return None
    try:
        value = get_state("unity_instance")
        if inspect.isawaitable(value):
            value = await value
        return str(value) if value else None
    except Exception:
        return None


def _render_result(
    envelope: dict[str, Any],
    response_format: ResponseFormat,
    verbosity: Verbosity,
) -> ToolResult:
    indent = 2 if verbosity == "detailed" else None
    text = json.dumps(envelope, ensure_ascii=False, indent=indent, separators=None if indent else (",", ":"))
    blocks = [TextContent(type="text", text=text)]

    if response_format == "structured":
        return ToolResult(content=[], structured_content=envelope)
    if response_format == "text":
        return ToolResult(content=blocks, structured_content=envelope)
    return ToolResult(content=blocks, structured_content=envelope)


def _canonicalize_tool_result(
    result: ToolResult,
    *,
    unity_instance: str | None,
    duration_ms: int,
) -> ToolResult:
    """Add the advertised envelope without discarding rich MCP content blocks."""
    payload = result.structured_content
    if payload is None:
        for block in result.content:
            if getattr(block, "type", None) != "text":
                continue
            try:
                candidate = json.loads(block.text)
            except (AttributeError, TypeError, json.JSONDecodeError):
                continue
            if isinstance(candidate, dict):
                payload = candidate
                break

    envelope = canonicalize_result(
        payload if payload is not None else {"success": True},
        unity_instance=unity_instance,
        duration_ms=duration_ms,
    )
    return ToolResult(
        content=result.content,
        structured_content=envelope,
        meta=result.meta,
    )


def _signature_with_controls(func: Callable[..., Any]) -> inspect.Signature:
    signature = inspect.signature(func)
    params = list(signature.parameters.values())
    names = {parameter.name for parameter in params}
    controls = []
    if "response_format" not in names:
        controls.append(inspect.Parameter(
            "response_format",
            inspect.Parameter.KEYWORD_ONLY,
            default="structured",
            annotation=RESPONSE_FORMAT_ANNOTATION,
        ))
    if "verbosity" not in names:
        controls.append(inspect.Parameter(
            "verbosity",
            inspect.Parameter.KEYWORD_ONLY,
            default="compact",
            annotation=VERBOSITY_ANNOTATION,
        ))

    var_keyword_index = next(
        (index for index, parameter in enumerate(params) if parameter.kind == inspect.Parameter.VAR_KEYWORD),
        len(params),
    )
    params[var_keyword_index:var_keyword_index] = controls
    return signature.replace(parameters=params, return_annotation=ToolResult)


def canonical_result(func: Callable[..., Any]) -> Callable[..., Any]:
    """Wrap a registered tool with canonical envelope and rendering controls."""
    tool_parameter_names = set(inspect.signature(func).parameters)
    injected_controls = {"response_format", "verbosity"} - tool_parameter_names
    original_signature = _signature_with_controls(func)

    @functools.wraps(func)
    async def _async_wrapper(*args: Any, **kwargs: Any) -> ToolResult:
        response_format = (
            kwargs.pop("response_format", "structured")
            if "response_format" in injected_controls
            else "structured"
        )
        verbosity = (
            kwargs.pop("verbosity", "compact")
            if "verbosity" in injected_controls
            else "compact"
        )
        start = time.perf_counter()
        result = await func(*args, **kwargs)
        duration_ms = round((time.perf_counter() - start) * 1000)

        unity_instance = await _unity_instance_from_call(args, kwargs)

        # Content-bearing results (screenshots, audio, files) retain their MCP
        # blocks and also receive the envelope promised by the output schema.
        if isinstance(result, ToolResult):
            return _canonicalize_tool_result(
                result,
                unity_instance=unity_instance,
                duration_ms=duration_ms,
            )

        envelope = canonicalize_result(
            result,
            unity_instance=unity_instance,
            duration_ms=duration_ms,
        )
        return _render_result(envelope, response_format, verbosity)

    _async_wrapper.__signature__ = original_signature
    _async_wrapper.__annotations__ = {**getattr(func, "__annotations__", {})}
    if "response_format" in injected_controls:
        _async_wrapper.__annotations__["response_format"] = RESPONSE_FORMAT_ANNOTATION
    if "verbosity" in injected_controls:
        _async_wrapper.__annotations__["verbosity"] = VERBOSITY_ANNOTATION
    _async_wrapper.__annotations__["return"] = ToolResult
    return _async_wrapper
