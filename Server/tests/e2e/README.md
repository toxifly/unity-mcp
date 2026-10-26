# Deterministic bridge E2E

`bridge_smoke.py` drives a **live Unity Editor** over the real wire path
(`send_command_with_retry`) with a fixed sequence of tool calls and exact
assertions. It is deterministic and needs no LLM or API key.

It is **not** collected by `pytest tests/` (the filename is not `test_*.py`), so
the normal unit suite never tries to reach a Unity instance.

## Run locally

Start a Unity Editor with the MCP bridge active, then:

```bash
cd Server
uv run python tests/e2e/bridge_smoke.py            # auto-discovers the instance
uv run python tests/e2e/bridge_smoke.py --instance MyProject@<hash>
```

Exit codes: `0` all steps passed · `1` a step failed an assertion (real bridge
regression) · `2` no Unity bridge reachable (setup problem, not a contract bug).

## Headless run

`tools/local_harness.py` boots headless Unity via
`MCPForUnity.Editor.McpCiBoot.StartStdioForCi`, waits for the bridge, then runs
this driver as its smoke leg. See the
[testing docs](../../../website/docs/contributing/testing.md#local-headless-test-harness).

## Adding steps

Append a `Step(...)` in `build_steps()` with a `check(resp)` callback that raises
`AssertionError` on failure. Use `_ok()` / `_result()` to stay tolerant of both
Unity response shapes. Keep new objects uniquely named (see the `_RUN` suffix) and
delete anything you create so reruns stay clean.
