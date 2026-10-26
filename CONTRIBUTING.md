# Contributing to MCP for Unity

Thanks for wanting to help! MCP for Unity is community-maintained and PRs of any size are welcome — bug fixes, new tools, docs improvements, tests.

## Quick Start

1. **Fork** this repo and **clone** your fork.
2. Branch off `develop`:
   ```bash
   git checkout -b feat/your-idea origin/develop
   ```
3. Install the dev environment (see [Dev Setup](website/docs/contributing/dev-setup.md)).
4. Make your change with tests.
5. Open a PR against `develop`.

## What We Look For

- **Tests for new behavior.** Python tests live in `Server/tests/`; Unity EditMode tests live in `TestProjects/UnityMCPTests/Assets/Tests/`.
- **Domain symmetry.** New tools live in *both* `Server/src/services/tools/manage_<domain>.py` (Python MCP tool) and `MCPForUnity/Editor/Tools/Manage<Domain>.cs` (C# implementation). See [Adding or Changing Tools and Resources](website/docs/contributing/dev-setup.md#adding-or-changing-tools-and-resources).
- **Minimal abstraction.** Three similar lines of code is better than a helper that's only used once.
- **Documentation as code.** Tool reference pages under `website/docs/reference/` are auto-generated — never hand-edit them outside the `<!-- examples:start --><!-- examples:end -->` blocks.

## Before You Push

```bash
# Python tests
cd Server && uv run pytest tests/ -v

# Unity multi-version compile check (versions in tools/unity-versions.json)
tools/check-unity-versions.sh

# Pre-commit hook for docs reference (one-time setup)
tools/install-hooks.sh
```

The pre-commit hook regenerates `website/docs/reference/` whenever you touch a tool/resource module, so the committed reference never goes stale.

## Pull Request Checklist

- [ ] Branched off `develop`
- [ ] New or updated tests
- [ ] Docs updated (the auto-gen handles the tool reference; narrative docs under `website/docs/` are hand-written)
- [ ] No commented-out code, no `// removed for X` markers, no `_unused` renames
- [ ] PR description explains the **why**, not just the **what**

## Code Style

- **Python:** type hints required; follow the patterns in existing `manage_*.py` files.
- **C#:** match existing namespace conventions under `MCPForUnity.Editor.*`; route Unity API differences through `MCPForUnity/Runtime/Helpers/Unity*Compat.cs` shims rather than `#if UNITY_*_OR_NEWER` blocks.
- **Markdown:** wrap at sensible widths; use sentence case in headings.

## Areas That Need Help

- Examples in tool reference pages (`website/docs/reference/tools/**/*.md` — add inside the `<!-- examples:start --><!-- examples:end -->` blocks).
- Net-new guide content (multi-instance routing, tool groups, transport modes).
- Translations beyond Chinese.
- Cross-platform shell testing for the CLI.

## Reporting Bugs / Requesting Features

Use the issue templates under [`.github/ISSUE_TEMPLATE/`](.github/ISSUE_TEMPLATE/). For security concerns, see [SECURITY.md](SECURITY.md) — do **not** open a public issue.

## Code of Conduct

See [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md). Be excellent to each other.

## Questions?

- [GitHub Issues](https://github.com/CoplayDev/unity-mcp/issues) — bugs, features
- [Discord](https://discord.gg/y4p8KfzrN4) — chat with maintainers and other contributors
- [Discussions](https://github.com/CoplayDev/unity-mcp/discussions) — design ideas, broad questions
