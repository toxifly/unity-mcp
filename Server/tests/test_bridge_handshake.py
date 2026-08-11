"""Compatibility contracts for the mandatory Python/Unity bridge handshake."""

from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path
from types import SimpleNamespace

try:
    import tomllib
except ModuleNotFoundError:  # pragma: no cover - Python 3.10
    import tomli as tomllib

import pytest

import core.bridge_handshake as handshake
from transport.models import RegisterMessage
from transport.plugin_hub import PluginHub
from transport.plugin_registry import PluginRegistry


REPO_ROOT = Path(__file__).resolve().parents[2]


def unity_handshake(
    *,
    version: str = "10.1.1-beta.1",
    protocol: int = handshake.BRIDGE_PROTOCOL_VERSION,
) -> dict:
    return {
        "bridge_protocol_version": protocol,
        "unity_package": {
            "version": version,
            "registered_resources": ["editor_state", "tool_states"],
            "tool_groups": ["core", "testing"],
        },
    }


def test_semver_and_pep440_prerelease_versions_are_equivalent():
    assert handshake.versions_are_compatible("10.1.1b1", "10.1.1-beta.1")
    assert not handshake.versions_are_compatible("10.1.0", "10.1.1-beta.1")


@pytest.mark.parametrize(
    ("server_version", "unity_version"),
    [
        ("10.1.1+build.7", "10.1.1"),
        ("10.1.1+build.7", "10.1.1+build.7"),
        ("10.1.1+build.7", "10.1.1+build.8"),
        ("10.1.1b1+build.7", "10.1.1-beta.1+build.7"),
        ("v10.1.1", "10.1.1"),
        ("v10.1.1", "v10.1.1"),
    ],
)
def test_metadata_and_leading_v_are_not_compatible_release_identities(
    server_version, unity_version
):
    assert not handshake.versions_are_compatible(server_version, unity_version)


def test_repository_server_and_package_versions_do_not_drift():
    unity_version = json.loads(
        (REPO_ROOT / "MCPForUnity" / "package.json").read_text(encoding="utf-8")
    )["version"]
    manifest = json.loads(
        (REPO_ROOT / "manifest.json").read_text(encoding="utf-8")
    )
    manifest_version = manifest["version"]
    with (REPO_ROOT / "Server" / "pyproject.toml").open("rb") as stream:
        server_version = tomllib.load(stream)["project"]["version"]

    assert manifest_version == unity_version
    assert handshake.versions_are_compatible(server_version, unity_version)

    manifest_args = manifest["server"]["mcp_config"]["args"]
    from_index = manifest_args.index("--from")
    assert manifest_args[from_index + 1] == (
        f"mcpforunityserver=={handshake.normalize_release_version(unity_version)}"
    )


def test_git_sha_override_is_validated(monkeypatch):
    monkeypatch.setenv("UNITY_MCP_SERVER_GIT_SHA", "abcdef1234567890")
    assert handshake.get_server_git_sha() == "abcdef1234567890"

    monkeypatch.setenv("UNITY_MCP_SERVER_GIT_SHA", "not-a-sha")
    with pytest.raises(handshake.BridgeCompatibilityError, match="hexadecimal Git SHA"):
        handshake.get_server_git_sha()


def test_git_sha_discovery_runs_once_per_process(monkeypatch):
    """SHA discovery spawns a git subprocess; it must stay off the per-request
    path (every port probe rebuilds the handshake)."""
    monkeypatch.delenv("UNITY_MCP_SERVER_GIT_SHA", raising=False)
    monkeypatch.setattr(handshake, "_discovered_git_sha", None)
    calls = []

    def fake_repo_sha():
        calls.append(1)
        return "abc1234"

    monkeypatch.setattr(handshake, "_repo_git_sha", fake_repo_sha)
    assert handshake.get_server_git_sha() == "abc1234"
    assert handshake.get_server_git_sha() == "abc1234"
    assert len(calls) == 1


def test_missing_handshake_fails_with_update_guidance():
    with pytest.raises(handshake.BridgeCompatibilityError, match="mandatory bridge handshake"):
        handshake.validate_unity_handshake(None, server_version="10.1.1b1")


def test_protocol_mismatch_reports_both_protocols():
    with pytest.raises(
        handshake.BridgeCompatibilityError,
        match=r"requires 2, Unity package sent 1",
    ):
        handshake.validate_unity_handshake(
            unity_handshake(protocol=1), server_version="10.1.1b1"
        )


def test_package_mismatch_reports_both_versions():
    with pytest.raises(
        handshake.BridgeCompatibilityError,
        match=r"Python server='10.1.0'.*Unity package='10.1.1-beta.1'",
    ):
        handshake.validate_unity_handshake(
            unity_handshake(), server_version="10.1.0"
        )


@pytest.mark.parametrize("field", ["registered_resources", "tool_groups"])
def test_unity_registration_lists_are_mandatory(field):
    payload = unity_handshake()
    payload["unity_package"][field] = []
    with pytest.raises(handshake.BridgeCompatibilityError, match=field):
        handshake.validate_unity_handshake(
            payload, server_version="10.1.1b1"
        )


def test_server_registration_gate_requires_capabilities_and_workflow(monkeypatch):
    monkeypatch.setattr(
        handshake,
        "get_server_resource_uris",
        lambda: ["mcpforunity://workflow"],
    )
    monkeypatch.setattr(handshake, "get_server_tool_groups", lambda: ["core"])
    with pytest.raises(
        handshake.BridgeCompatibilityError,
        match="mcpforunity://capabilities",
    ):
        handshake.validate_server_registrations()


def test_fresh_process_probe_handshake_advertises_full_catalog():
    """Transport-only entrypoints (stdio port probe, raw bridge smoke) never
    call create_mcp_server(), so the handshake must not depend on FastMCP
    registration having run in-process."""
    script = (
        "import json, sys\n"
        "from transport.legacy.port_discovery import PortDiscovery  # noqa: F401\n"
        "from core.bridge_handshake import build_server_handshake\n"
        "json.dump(build_server_handshake()['python_server'], sys.stdout)\n"
    )
    env = dict(os.environ)
    env["PYTHONPATH"] = str(REPO_ROOT / "Server" / "src")
    completed = subprocess.run(
        [sys.executable, "-c", script],
        capture_output=True,
        text=True,
        env=env,
        timeout=120,
    )
    assert completed.returncode == 0, completed.stderr
    manifest = json.loads(completed.stdout)
    assert handshake.REQUIRED_SERVER_RESOURCES <= set(manifest["registered_resources"])
    assert "core" in manifest["tool_groups"]


def test_banner_manifest_contains_unity_identity_and_registrations():
    parsed = handshake.parse_banner_manifest(
        "WELCOME UNITY-MCP 2 FRAMING=1 BRIDGE_PROTOCOL=2 "
        "UNITY_PACKAGE=10.1.1-beta.1 RESOURCES=editor_state,tool_states "
        "TOOL_GROUPS=core,testing"
    )
    assert parsed == unity_handshake()


@pytest.mark.asyncio
async def test_websocket_registration_rejects_incompatible_package_before_session():
    class FakeWebSocket:
        def __init__(self):
            self.state = SimpleNamespace()
            self.sent = []
            self.closed = None

        async def send_json(self, payload):
            self.sent.append(payload)

        async def close(self, code, reason=None):
            self.closed = (code, reason)

    registry = PluginRegistry()
    PluginHub.configure(registry)
    websocket = FakeWebSocket()
    hub = PluginHub.__new__(PluginHub)
    payload = RegisterMessage(
        project_name="DriftedProject",
        project_hash="deadbeef",
        unity_version="2022.3",
        handshake=unity_handshake(version="99.0.0"),
    )

    try:
        await hub._handle_register(websocket, payload)
        assert websocket.sent[0]["type"] == "handshake_error"
        assert "Server/package version mismatch" in websocket.sent[0]["error"]
        assert websocket.closed == (4406, "Incompatible Unity MCP bridge")
        assert await registry.list_sessions() == {}
    finally:
        PluginHub._registry = None
        PluginHub._lock = None
        PluginHub._loop = None
