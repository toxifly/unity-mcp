"""Startup tool-visibility contract: lean discovery must survive Unity sync.

Regression coverage for the bug where Unity's legacy per-tool defaults
(``AutoRegister || IsBuiltIn``) reported every built-in tool as enabled, and
the startup sync then re-enabled every optional group (~54 tools instead of
the core/meta subset).

The full sequence runs in an isolated subprocess (see
``_startup_visibility_script.py``) because integration/conftest.py installs a
process-global fastmcp stub during collection.
"""
import os
import subprocess
import sys


def test_startup_visibility_sequence():
    script = os.path.join(os.path.dirname(__file__), "_startup_visibility_script.py")
    env = os.environ.copy()
    env.update({
        "UNITY_MCP_SKIP_STARTUP_CONNECT": "1",
        "DISABLE_TELEMETRY": "true",
    })
    result = subprocess.run(
        [sys.executable, script],
        cwd=os.path.dirname(os.path.dirname(__file__)),
        env=env,
        capture_output=True,
        text=True,
        timeout=60,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    assert "OK" in result.stdout, result.stdout
