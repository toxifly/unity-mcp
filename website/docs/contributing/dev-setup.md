# MCP for Unity - Developer Guide

| English | [简体中文](https://github.com/toxifly/unity-mcp/blob/develop/docs/development/README-DEV-zh.md) |
|---------------------------|------------------------------|

## Contributing

**Branch off `develop`** and open PRs against `develop`.

Before proposing major new features, please reach out to discuss - someone may already be working on it or it may have been considered previously. Open an issue or discussion to coordinate.

Keep PRs focused. A small fix with a clear repro and the exact Unity/package versions is much easier to review than a broad cleanup bundled with behavior changes.

For bug fixes, include:

- Unity version(s) tested
- the unity-mcp commit you tested (Unity loads the package and the server from the same checkout)
- commands/tests run locally

Avoid mixing version bumps with feature or bug-fix PRs; bump versions in a separate change with `tools/update_versions.py`.

## Repository Map

- `MCPForUnity/` - the Unity package: Editor UI, C# tool handlers, resources, compatibility shims, package metadata.
- `Server/` - the Python MCP server, CLI, FastMCP tool/resource registry, transports, and server-side tests.
- `TestProjects/UnityMCPTests/` - Unity project used for EditMode/PlayMode tests. Its manifest points at `file:../../../MCPForUnity`.
- `docs/` - user, contributor, release, migration, and reference documentation.
- `tools/` - version bumps, docs reference generation, git hooks, the local test harness, Unity-version checks, and stress tools.
- `CustomTools/` - examples and support code for project-defined custom tools.

## Local Development Setup

### 0. Prepare the Python Server

For server work, set up the environment and run the tests:

```bash
cd Server
uv sync --extra dev
uv run pytest tests/ -v --tb=short
```

Most Python unit tests do not need Unity running. Integration tests that talk to the Editor require a Unity instance with the bridge connected.

### 1. Point Unity to Your Local Server

For the fastest iteration when working on the Python server:

1. Open Unity and go to **Window > MCP for Unity**
2. Open **Settings > Advanced Settings**
3. Set **Server Source** to your local `Server/` directory path. It is required: clients and the local HTTP server run that checkout with `uv run --frozen --project`, so code edits take effect on the next server start. After dependency changes, run `uv lock` in `Server/`.

### 2. Point a Unity Project at Your Checkout

Add a `file:` dependency on this checkout's `MCPForUnity/` folder to the project's `Packages/manifest.json`:

```json
"com.coplaydev.unity-mcp": "file:/path/to/unity-mcp/MCPForUnity"
```

Unity re-resolves packages on its next import. Do not patch `Library/PackageCache/` directly; Unity may overwrite it on the next resolve.

## Adding or Changing Tools and Resources

Built-in Unity tools usually have two sides:

- a C# handler under `MCPForUnity/Editor/Tools/`
- a Python-facing tool under `Server/src/services/tools/`

Resources follow the same split:

- C# resources under `MCPForUnity/Editor/Resources/`
- Python resources under `Server/src/services/resources/`

Use `[McpForUnityTool]` / `[McpForUnityResource]` on the Unity side and `@mcp_for_unity_tool` / `@mcp_for_unity_resource` on the server side. If the tool is long-running, use the existing `PendingResponse` / polling patterns instead of blocking the bridge.

Current server tool groups are:

- `core`
- `docs`
- `vfx`
- `animation`
- `ui`
- `scripting_ext`
- `testing`
- `probuilder`
- `profiling`

Tools with `group=None` are server meta-tools and are always visible. The default enabled group is `core`; other groups can be enabled through the Tools tab or `manage_tools`.

When adding, removing, or renaming tools/resources, regenerate the reference pages (see [Docs Workflow](./docs.md)) and update `unity-mcp-skill/` if agents need new guidance.

## Tool Selection & the Meta-Tool

MCP for Unity organizes tools into **groups**. You can selectively enable or disable tools to control which capabilities are exposed to AI clients — reducing context window usage and focusing the AI on relevant tools.

### Using the Tools Tab in the Editor

Open **Window > MCP for Unity** and switch to the **Tools** tab. Each tool group is displayed as a collapsible foldout with:

- **Per-tool toggles** — click individual tool toggles to enable or disable them.
- **Group checkbox** — a checkbox embedded directly in each group's foldout header (next to the group title) enables or disables all tools in that group at once without expanding or collapsing the foldout.
- **Enable All / Disable All** — global buttons to toggle all tools.
- **Rescan** — re-discovers tools from assemblies (useful after adding new `[McpForUnityTool]` classes).
- **Reconfigure Clients** — re-registers tools with the server and reconfigures all detected MCP clients in one click, applying your changes without navigating back to the Clients tab.

### How Changes Propagate

Tool visibility changes work differently depending on the transport mode:

**HTTP mode** (recommended):

1. Toggling a tool calls `ReregisterToolsAsync()`, which sends the updated enabled tool list to the Python server over WebSocket.
2. The server updates its internal tool visibility via `mcp.enable()`/`mcp.disable()` per group.
3. The server sends a `tools/list_changed` MCP notification to all connected client sessions.
4. Already-connected clients (Claude Desktop, VS Code, etc.) automatically receive the updated tool list.

**Stdio mode**:

1. Toggles are persisted locally but cannot be pushed to the server (no WebSocket connection).
2. The server starts with group-based defaults — only `core` is enabled. After changing toggles, ask the AI to run `manage_tools` with `action='sync'` — this pulls the current tool states from Unity and syncs server visibility.
3. Alternatively, restart the server to pick up changes.

In both modes, effective visibility for a session resolves in order: session-level `manage_tools` `activate`/`deactivate` overrides first, then the last state synced from Unity, then the group-based defaults (only `core` enabled). `manage_tools(action='reset')` clears the session overrides.

### The `manage_tools` Meta-Tool

The server exposes a built-in `manage_tools` tool (always visible, not group-gated) that AIs can call directly:

| Action | Description |
|--------|-------------|
| `list_groups` | Lists all tool groups with their tools and enable/disable status |
| `activate` | Enables a tool group by name (e.g., `group="vfx"`) |
| `deactivate` | Disables a tool group by name |
| `sync` | Pulls current tool states from Unity and syncs server visibility (essential for stdio mode) |
| `reset` | Restores default tool visibility |

### When You Need to Reconfigure

After toggling tools on/off, MCP clients need to learn about the changes:

- **HTTP mode**: Changes propagate automatically via `tools/list_changed`. Most clients pick this up immediately. If a client doesn't, click **Reconfigure Clients** on the Tools tab, or go to Clients tab and click Configure.
- **Stdio mode**: The server process needs to be told about changes. Either ask the AI to call `manage_tools(action='sync')`, or restart the MCP session. Click **Reconfigure Clients** to re-register all clients with updated config.

## Running Tests

All major new features (and some minor ones) must include test coverage. It's so easy to get LLMs to write tests, ya gotta do it!

### Python Tests 

Located in `Server/tests/`:

```bash
cd Server
uv run pytest tests/ -v
```

Useful narrower runs:

```bash
uv run pytest tests/test_manage_camera.py -v
uv run pytest tests/integration/test_run_tests_async.py -v
uv run pytest tests/ -v --tb=short
```

### Unity C# Tests

Located in `TestProjects/UnityMCPTests/Assets/Tests/`.

The test project consumes the local Unity package from this repo. Use it for package import/compile checks before opening a PR that touches `MCPForUnity/Runtime/`, `MCPForUnity/Editor/`, or package metadata.

**Using the CLI** (requires Unity running with MCP bridge connected):

```bash
cd Server

# Run EditMode tests (default)
uv run python -m cli.main editor tests

# Run PlayMode tests
uv run python -m cli.main editor tests --mode PlayMode

# Run async and poll for results (useful for long test runs)
uv run python -m cli.main editor tests --async
uv run python -m cli.main editor poll-test <job_id> --wait 60

# Show only failed tests
uv run python -m cli.main editor tests --failed
```

**Using MCP tools directly** (from any MCP client):

```
run_tests(mode="EditMode")
run_tests(mode="PlayMode", init_timeout=120000)  # PlayMode may need longer init due to domain reload
get_test_job(job_id="<id>", wait_timeout=60)
```

### Local headless test harness

`uv run --project Server python tools/local_harness.py` boots a headless Editor and runs the smoke + EditMode + PlayMode legs over the bridge. See [Testing → Local headless test harness](./testing.md#local-headless-test-harness) for the flags and exit-code contract.

### Code Coverage

```bash
cd Server
uv run pytest tests/ --cov --cov-report=html
open htmlcov/index.html
```

## Compatibility Notes

The Unity package currently declares Unity `2021.3` as its minimum version in `MCPForUnity/package.json`. Code that uses Unity API conditionals (`UNITY_2022_3_OR_NEWER`, `UNITY_2023_1_OR_NEWER`, `UNITY_6000_*`) should be tested against the affected editor line, not just the oldest supported version.

For compatibility PRs, note the exact editor versions you tested in the PR body.

## Troubleshooting During Development

- **Unity opens in Safe Mode after changing package source**: the package failed to compile before MCP can start. Fix the compile errors first; the MCP server cannot recover from package compile failures.
- **Server changes are not picked up**: make sure **Server Source** points to your local `Server/` directory, then restart the server (for stdio, restart the MCP client session). After dependency changes, run `uv lock` in `Server/`.
- **Stdio tool visibility looks stale**: call `manage_tools(action="sync")` or restart the MCP session. HTTP mode can push `tools/list_changed` notifications automatically.
- **Multiple Unity editors are open**: use `mcpforunity://instances` and `set_active_instance` to confirm which project the server is targeting.
## Unity-version matrix

`tools/unity-versions.json` lists the Unity versions the package is checked against, to catch breaks in `#if UNITY_*_OR_NEWER` branches. The `floor` role (`2021.3.45f2`) is the package minimum. `defaultVersion` (currently Unity 6.0 LTS, `6000.0.75f1`) is the Editor `tools/local_harness.py` boots when the target project has no `ProjectVersion.txt`.

## Checking the matrix locally

`tools/check-unity-versions.sh` reads the same file. This fork has no CI, so run it before pushing changes to compat shims, conditional compilation, or anything else version-sensitive.

The script has two runners. Use whichever fits your setup:

| Runner | When to use | Cost |
|---|---|---|
| **Local Unity Hub** (default) | You already have one or more matrix versions installed via Unity Hub. Fastest. | Disk: each editor is 3-6 GB. |
| **GameCI Docker** (`--docker`) | You don't want to install every editor locally. Uses the GameCI editor images. | One-time pull is 5-15 GB per version. On Apple Silicon, expect ~5-10× slowdown from amd64 emulation. |

### Local Unity Hub mode

```bash
# Compile-only check across every locally-installed Unity in the matrix (~30-60s warm per version).
tools/check-unity-versions.sh

# Full EditMode test run on each installed version.
tools/check-unity-versions.sh --full

# Filter to one version family.
tools/check-unity-versions.sh --only 6000.0
```

Windows uses the PowerShell companion:

```powershell
pwsh .\tools\check-unity-versions.ps1
pwsh .\tools\check-unity-versions.ps1 -Full
pwsh .\tools\check-unity-versions.ps1 -Only 6000.0
```

Versions not installed via Unity Hub are skipped — the script never forces you to install every editor in the matrix. Install the versions you most care about (typically the floor `2021.3.45f2` and your daily-driver Unity 6) and use Docker mode for the rest.

### GameCI Docker mode (no Unity Hub install required)

```bash
tools/check-unity-versions.sh --docker                # all matrix versions, compile-only
tools/check-unity-versions.sh --docker --full         # full EditMode run
tools/check-unity-versions.sh --docker --only 2022.3  # one version family
```

```powershell
pwsh .\tools\check-unity-versions.ps1 -Docker
pwsh .\tools\check-unity-versions.ps1 -Docker -Full
```

**One-time setup: get a Unity license**

GameCI containers still need an activated Unity license. Free Personal activations work fine and are tied to the machine, not the editor version — so a single `.ulf` covers every version in the matrix.

```bash
# 1. Generate the request file (.alf) — outputs Unity_v<version>.alf in the current directory.
docker run --rm -v "$PWD":/work unityci/editor:ubuntu-2021.3.45f2-base-3 \
  /opt/unity/Editor/Unity -batchmode -nographics -quit \
  -createManualActivationFile -logFile /dev/stdout

# 2. Upload Unity_v<version>.alf at https://license.unity3d.com/manual
#    Choose Personal license → save the resulting .ulf file.

# 3. Export the .ulf contents (add to ~/.zshrc or ~/.bashrc to persist):
export UNITY_LICENSE="$(cat /path/to/Unity_v<version>.ulf)"

# 4. Run the check.
tools/check-unity-versions.sh --docker
```

PowerShell equivalent for step 3: `$env:UNITY_LICENSE = Get-Content C:\path\to\Unity_v<version>.ulf -Raw`.

**Coverage gap on Apple Silicon Macs**: GameCI publishes only `linux/amd64` images. Docker Desktop runs them under Rosetta/QEMU at ~5-10× the native amd64 speed. A compile that takes 30s on Intel takes 3-5 min on M-series. Still faster than installing four Unity editors, but plan for it.

**Opt-in pre-push hook**

```bash
tools/install-hooks.sh             # installs .git/hooks/pre-push (idempotent)
tools/install-hooks.sh --uninstall # removes our hooks
```

Once installed, `git push` runs the compile-only check first when the push touches `MCPForUnity/Editor/**`, `MCPForUnity/Runtime/**`, `TestProjects/UnityMCPTests/**`, or `tools/unity-versions.json`. Pushes that touch only docs, Server/, or unrelated files skip the check.

To bypass for a single push: `git push --no-verify`.

**Bumping the matrix**

Edit `tools/unity-versions.json`; both check scripts and the local harness read it. Every id needs a GameCI image for `--docker` mode (see the file's `$comment`).

