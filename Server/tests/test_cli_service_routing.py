import asyncio
from unittest.mock import AsyncMock

import pytest

from services.cli_service import invoke_cli_service
import services.tools.manage_editor as manage_editor_mod


def test_define_service_route_invokes_manage_editor_with_bound_instance(monkeypatch):
    invoke = AsyncMock(return_value={"success": True, "message": "ok"})
    monkeypatch.setattr(manage_editor_mod, "manage_editor", invoke)

    result = asyncio.run(invoke_cli_service(
        "manage_editor",
        {"action": "set_scripting_defines", "defines": ["FEATURE"], "target": "android"},
        "Project@abc123",
    ))

    assert result["success"] is True
    ctx = invoke.await_args.args[0]
    assert asyncio.run(ctx.get_state("unity_instance")) == "Project@abc123"
    assert invoke.await_args.kwargs == {
        "action": "set_scripting_defines",
        "defines": ["FEATURE"],
        "target": "android",
    }


def test_service_route_rejects_commands_without_recovery_semantics():
    with pytest.raises(ValueError, match="only supported"):
        asyncio.run(invoke_cli_service(
            "manage_editor", {"action": "play"}, "Project@abc123"))
