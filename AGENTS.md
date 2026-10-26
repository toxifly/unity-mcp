# AGENTS.md

These repository instructions apply to every agent and all subdirectories.

## Mandatory Local-Server Invariant

The Unity package and Python server are released and validated as one unit: MCP for Unity runs only the local `Server/` checkout from the same revision as the package.

- Every client registration and the local HTTP server run `uv run --frozen --project <Server/> mcp-for-unity`, where `<Server/>` is the **Server Source** setting. The checkout's `Server/uv.lock` pins every dependency.
- Server Source is required. When it is unset, or is not a local directory named `Server` that contains `pyproject.toml`, stop with an actionable error.
- Never run, register, or fall back to a published server: no `mcpforunityserver` package from PyPI, `uvx --from`, git URL, version range, prerelease channel, or `latest` lookup.
- Status checks treat a stdio registration as current only when its `--project` argument is the Server Source directory.
- Keep `MCPForUnity/package.json`, `Server/pyproject.toml`, and `Server/uv.lock` version-aligned.
- Any change to server-command generation must retain or add tests proving the command is the locked local run and that no published-package path remains.

Do not weaken this invariant to preserve backward compatibility. The mandatory bridge handshake is expected to reject mismatched versions.

## Project Test-Stage Enforcement

Projects can opt into enforced test ordering with `ProjectSettings/McpTestStages.json` (non-empty
`inputs` paths relative to the project). `TestJobManager.StartJob` applies `TestStageGuard` for MCP,
batch and bridge CLI calls. Full suites need a successful focused test/group/category run with at
least one passing test for unchanged inputs; assembly-only selection is a full suite. A failed full
suite blocks other full-suite modes until repaired. Focused diagnosis remains allowed.

The guard hashes configured input contents/paths and persists ordering evidence under `Library/`.
It does not skip suites or infer test relevance. Projects without the config are unaffected.
The consuming Chaos Fusion project's `McpTestStagesTests` exercises the installed guard, including
failure recovery and source-change invalidation; run that focused fixture before its full Unity gates.
