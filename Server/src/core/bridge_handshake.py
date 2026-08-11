"""Mandatory compatibility handshake for the Python/Unity bridge."""

from __future__ import annotations

import json
import os
from pathlib import Path
import re
import subprocess
from typing import Any, Mapping

from core.telemetry import get_package_version
from services.capability_catalog import (
    get_catalog_resource_uris,
    get_catalog_tool_groups,
)


BRIDGE_PROTOCOL_VERSION = 2
REQUIRED_SERVER_RESOURCES = frozenset({
    "mcpforunity://capabilities",
    "mcpforunity://workflow",
})


class BridgeCompatibilityError(RuntimeError):
    """Raised before bridge registration when either peer is incompatible."""


def normalize_release_version(value: str) -> str:
    """Normalize one exact supported SemVer/PEP 440 release identity."""
    normalized = (value or "").lower()
    replacements = {
        "alpha": "a",
        "beta": "b",
        "preview": "rc",
        "pre": "rc",
    }
    match = re.fullmatch(
        r"(?P<base>(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*))"
        r"(?:(?:-(?P<semver_label>alpha|beta|preview|pre|rc|a|b)"
        r"(?:\.(?P<semver_num>0|[1-9]\d*))?)|"
        r"(?P<pep_label>a|b|rc)(?P<pep_num>0|[1-9]\d*))?",
        normalized,
    )
    if not match:
        return ""
    label = match.group("pep_label") or match.group("semver_label")
    if not label:
        return match.group("base")
    label = replacements.get(label, label)
    number = match.group("pep_num") or match.group("semver_num") or "0"
    return f"{match.group('base')}{label}{number}"


def versions_are_compatible(server_version: str, unity_package_version: str) -> bool:
    """Package and server are released together and must identify the same build."""
    server = normalize_release_version(server_version)
    unity = normalize_release_version(unity_package_version)
    return bool(server and unity and server != "unknown" and unity != "unknown" and server == unity)


def _repo_git_sha() -> str | None:
    current = Path(__file__).resolve()
    for candidate in current.parents:
        if not (candidate / ".git").exists():
            continue
        git_path = candidate.as_posix()
        # stdin must NOT be inherited: the MCP stdio transport keeps a thread
        # blocked reading the server's stdin pipe, and a git child that
        # inherits that pipe deadlocks on Windows before doing any work.
        # Popen instead of subprocess.run because run()'s timeout path reaps
        # the killed child with an unbounded communicate() that hangs on the
        # same inherited-pipe condition.
        try:
            process = subprocess.Popen(
                [
                    "git",
                    "-c",
                    f"safe.directory={git_path}",
                    "-C",
                    git_path,
                    "rev-parse",
                    "HEAD",
                ],
                stdin=subprocess.DEVNULL,
                stdout=subprocess.PIPE,
                stderr=subprocess.DEVNULL,
                text=True,
            )
        except OSError:
            return None
        try:
            stdout, _ = process.communicate(timeout=2)
        except subprocess.SubprocessError:
            process.kill()
            return None
        if process.returncode != 0:
            return None
        sha = stdout.strip().lower()
        return sha if re.fullmatch(r"[0-9a-f]{7,64}", sha) else None
    return None


def _installed_git_sha() -> str | None:
    """Read PEP 610 VCS provenance when the server came from a Git install."""
    try:
        from importlib import metadata

        distribution = metadata.distribution("mcpforunityserver")
        direct_url = distribution.read_text("direct_url.json")
        if not direct_url:
            return None
        commit_id = json.loads(direct_url).get("vcs_info", {}).get("commit_id", "")
        commit_id = str(commit_id).strip().lower()
        return commit_id if re.fullmatch(r"[0-9a-f]{7,64}", commit_id) else None
    except Exception:
        return None


# Discovered once per process: the SHA cannot change for a running server, and
# discovery spawns a git subprocess that must stay off the per-request path
# (the bridge handshake is rebuilt on every port probe and instance discovery).
_discovered_git_sha: str | None = None


def get_server_git_sha() -> str:
    """Resolve build provenance from an override, checkout, or PEP 610 metadata."""
    override = os.environ.get("UNITY_MCP_SERVER_GIT_SHA", "").strip().lower()
    if override:
        if not re.fullmatch(r"[0-9a-f]{7,64}", override):
            raise BridgeCompatibilityError(
                "UNITY_MCP_SERVER_GIT_SHA must be a 7-64 character hexadecimal Git SHA"
            )
        return override
    global _discovered_git_sha
    if _discovered_git_sha is None:
        _discovered_git_sha = _repo_git_sha() or _installed_git_sha() or "unknown"
    return _discovered_git_sha


def get_server_resource_uris() -> list[str]:
    """Advertise from the build's capability catalog, not FastMCP state.

    Transport-only callers (stdio port probe, raw bridge smoke) never
    construct a FastMCP server, so the manifest must not depend on one.
    """
    return get_catalog_resource_uris()


def get_server_tool_groups() -> list[str]:
    return get_catalog_tool_groups()


def build_server_manifest() -> dict[str, Any]:
    return {
        "version": get_package_version(),
        "git_sha": get_server_git_sha(),
        "registered_resources": get_server_resource_uris(),
        "tool_groups": get_server_tool_groups(),
    }


def build_server_handshake() -> dict[str, Any]:
    """Build the Python half sent before any command on a bridge socket."""
    return {
        "bridge_protocol_version": BRIDGE_PROTOCOL_VERSION,
        "python_server": build_server_manifest(),
    }


def validate_server_registrations() -> None:
    resources = set(get_server_resource_uris())
    missing = sorted(REQUIRED_SERVER_RESOURCES - resources)
    if missing:
        raise BridgeCompatibilityError(
            "Python server registration is incomplete; missing mandatory resources: "
            f"{', '.join(missing)}. The running server package is stale or was built incorrectly."
        )
    if not get_server_tool_groups():
        raise BridgeCompatibilityError(
            "Python server registration is incomplete; no MCP tool groups were registered."
        )

    # Post-registration verification: the constructed FastMCP server must
    # actually serve the mandatory resources the catalog advertises.
    from services.resources import get_active_resource_uris

    missing_active = sorted(
        REQUIRED_SERVER_RESOURCES - set(get_active_resource_uris()))
    if missing_active:
        raise BridgeCompatibilityError(
            "FastMCP registration did not attach mandatory resources advertised "
            f"by the capability catalog: {', '.join(missing_active)}."
        )


def build_bridge_handshake(unity_package: Mapping[str, Any]) -> dict[str, Any]:
    return {
        "bridge_protocol_version": BRIDGE_PROTOCOL_VERSION,
        "python_server": build_server_manifest(),
        "unity_package": dict(unity_package),
    }


def validate_unity_handshake(
    handshake: Mapping[str, Any] | None,
    *,
    server_version: str | None = None,
) -> dict[str, Any]:
    """Validate Unity's half of the handshake and return its manifest."""
    if not isinstance(handshake, Mapping):
        raise BridgeCompatibilityError(
            "Unity package did not send the mandatory bridge handshake. "
            f"Bridge protocol {BRIDGE_PROTOCOL_VERSION} is required; update the Unity package."
        )

    protocol = handshake.get("bridge_protocol_version")
    if protocol != BRIDGE_PROTOCOL_VERSION:
        raise BridgeCompatibilityError(
            "Bridge protocol mismatch: "
            f"Python server requires {BRIDGE_PROTOCOL_VERSION}, Unity package sent {protocol!r}. "
            "Update the Python server and Unity package together."
        )

    unity_package = handshake.get("unity_package")
    if not isinstance(unity_package, Mapping):
        raise BridgeCompatibilityError(
            "Unity handshake is incomplete: missing unity_package manifest."
        )

    unity_version = str(unity_package.get("version") or "")
    actual_server_version = server_version or get_package_version()
    if not versions_are_compatible(actual_server_version, unity_version):
        raise BridgeCompatibilityError(
            "Server/package version mismatch: "
            f"Python server={actual_server_version!r}, Unity package={unity_version or 'missing'!r}. "
            "Install matching releases and restart both the MCP server and Unity bridge."
        )

    for field in ("registered_resources", "tool_groups"):
        value = unity_package.get(field)
        if not isinstance(value, list) or not value or not all(
            isinstance(item, str) and item for item in value
        ):
            raise BridgeCompatibilityError(
                f"Unity handshake is incomplete: {field} must be a non-empty string list."
            )

    return dict(unity_package)


def validate_bridge_acknowledgement(
    response: Mapping[str, Any],
    unity_package: Mapping[str, Any],
    server_handshake: Mapping[str, Any],
) -> dict[str, Any]:
    """Validate Unity's response to the Python half of the bridge handshake."""
    if response.get("status") != "success":
        error = response.get("error") or "Unity rejected the bridge handshake"
        raise BridgeCompatibilityError(str(error))

    acknowledged = response.get("result")
    if not isinstance(acknowledged, Mapping):
        raise BridgeCompatibilityError(
            "Unity returned an invalid bridge handshake acknowledgement"
        )

    acknowledged_unity = validate_unity_handshake(acknowledged)
    expected_server = server_handshake.get("python_server")
    if not isinstance(expected_server, Mapping):
        raise BridgeCompatibilityError(
            "Python sent an invalid bridge handshake manifest"
        )
    if acknowledged.get("python_server") != expected_server:
        raise BridgeCompatibilityError(
            "Unity bridge handshake acknowledgement did not echo the Python server manifest"
        )
    if acknowledged_unity != dict(unity_package):
        raise BridgeCompatibilityError(
            "Unity bridge handshake acknowledgement did not echo the banner manifest"
        )
    return {
        "bridge_protocol_version": BRIDGE_PROTOCOL_VERSION,
        "python_server": dict(expected_server),
        "unity_package": acknowledged_unity,
    }


def parse_banner_manifest(banner: str) -> dict[str, Any]:
    """Convert the stdio bridge banner into Unity's structured handshake half."""
    tokens = dict(re.findall(r"\b([A-Z_]+)=([^\s]+)", banner))
    protocol_raw = tokens.get("BRIDGE_PROTOCOL")
    try:
        protocol = int(protocol_raw) if protocol_raw is not None else None
    except ValueError:
        protocol = protocol_raw

    def split_token(name: str) -> list[str]:
        raw = tokens.get(name, "")
        return sorted({item for item in raw.split(",") if item})

    return {
        "bridge_protocol_version": protocol,
        "unity_package": {
            "version": tokens.get("UNITY_PACKAGE", ""),
            "registered_resources": split_token("RESOURCES"),
            "tool_groups": split_token("TOOL_GROUPS"),
        },
    }
