<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/images/logo-header-dark.png">
    <img alt="MCP for Unity" src="docs/images/logo-header-light.png" width="400">
  </picture>
</p>

<div align="center">

[English](README.md) <img src="docs/images/connector.svg" alt="↔" height="14"> [简体中文](docs/i18n/README-zh.md) &nbsp;&nbsp;&nbsp;|&nbsp;&nbsp;&nbsp; [Discord](https://discord.gg/y4p8KfzrN4) <img src="docs/images/connector.svg" alt="↔" height="14"> [Docs](website/docs/)

#### Proudly sponsored and maintained by [Aura](https://www.tryaura.dev/) — the AI assistant for Unreal & Unity.
##### And don't miss [Godot AI](https://github.com/hi-godot/godot-ai), the new open source project from the makers of MCP for Unity.

</div>

<p align="center"><b>Create your Unity apps with LLMs.</b> MCP for Unity bridges AI assistants — Claude, Codex, VS Code, local LLMs, and more — with your Unity Editor via <a href="https://modelcontextprotocol.io/introduction">Model Context Protocol</a>. Give your LLM the tools to manage assets, control scenes, edit scripts, run tests, and automate your game dev workflows.</p>

<p align="center">
  <img alt="MCP for Unity building a scene" src="docs/images/building_scene.gif">
</p>

---

<details>
<summary><strong>Recent Updates</strong></summary>

* **[v10.0.0](https://github.com/CoplayDev/unity-mcp/releases/tag/v10.0.0)** (2026-06-30)
* **[v9.7.3](https://github.com/CoplayDev/unity-mcp/releases/tag/v9.7.3)** (2026-06-15)
* **[v9.7.1](https://github.com/CoplayDev/unity-mcp/releases/tag/v9.7.1)** (2026-05-24)
* **[v9.7.0](https://github.com/CoplayDev/unity-mcp/releases/tag/v9.7.0)** (2026-05-22)
* **[v9.6.8](https://github.com/CoplayDev/unity-mcp/releases/tag/v9.6.8)** (2026-04-27)

Full history: [Release Notes](website/docs/releases.md).

</details>

---

## What it does

Control the Unity Editor in natural language from any MCP client — create scenes & GameObjects, edit C# scripts, manage assets, run tests, profile, and build. 47 focused MCP tool entrypoints, any client, free & MIT.

**[Browse the full tool catalog →](website/docs/reference/tools/index.md)**

---

## Quickstart

**Requirements:** Unity **2021.3 LTS → 6.x** · Python **3.10+** (via [`uv`](https://docs.astral.sh/uv/)). Works with **any MCP client** — Claude Desktop & Code, Cursor, VS Code, Windsurf, Cline, Gemini CLI, and more.

1. **Install** — clone this repository, then Unity → Package Manager → Add package from disk → the clone's `MCPForUnity/package.json`.
2. **Configure** — set **Server Source** (Advanced Settings) to the checkout's `Server/` directory, then `Window → MCP for Unity → Configure All Detected Clients`.
3. **Prompt** — *"Create a cube at the origin and add a Rigidbody."* The cube appears in seconds.

---

## Server source policy

MCP for Unity runs only a local server checkout. Set **Server Source** to the `Server/` directory of the same unity-mcp checkout the package comes from. Client registrations and the local HTTP server run it with `uv run --frozen --project <Server/> mcp-for-unity`, so `Server/uv.lock` pins every dependency. Configuration fails with an error while Server Source is unset or is not a local `Server/` directory; MCP for Unity never installs or runs a published `mcpforunityserver` package.

---

## Community

- [Discord](https://discord.gg/y4p8KfzrN4) — chat with maintainers and other contributors
- [Issues](https://github.com/CoplayDev/unity-mcp/issues) — bugs and feature requests
- [Discussions](https://github.com/CoplayDev/unity-mcp/discussions) — design ideas and broader questions
- Security: see [SECURITY.md](SECURITY.md) for private reporting

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Branch off `develop` and open PRs against it. The full dev setup and testing docs live in [Contributing](website/docs/contributing/dev-setup.md).

## Advanced

- **Multiple Unity instances** — [Multi-Instance Routing](website/docs/guides/multi-instance.md)
- **Tool groups (vfx / animation / ui / testing / etc.)** — [Tool Groups](website/docs/guides/tool-groups.md)
- **v10 asset generation and upgrade notes** — [v10 Migration](website/docs/migrations/v10.md)
- **Roslyn script validation** — [Roslyn Validation](website/docs/guides/roslyn.md)
- **Remote-hosted server with auth** — [Remote Server Auth](website/docs/guides/remote-server-auth.md)

## Star History

[![Star History Chart](https://api.star-history.com/svg?repos=CoplayDev/unity-mcp&type=Date)](https://www.star-history.com/#CoplayDev/unity-mcp&Date)

## Citation

If MCP for Unity helped your research, please cite it.

```bibtex
@inproceedings{wu2025mcpunity,
  author    = {Wu, Shutong and Barnett, Justin P.},
  title     = {{MCP-Unity}: {Protocol-Driven} Framework for Interactive {3D} Authoring},
  year      = {2025},
  isbn      = {9798400721366},
  publisher = {Association for Computing Machinery},
  address   = {New York, NY, USA},
  url       = {https://doi.org/10.1145/3757376.3771417},
  doi       = {10.1145/3757376.3771417},
  series    = {SA Technical Communications '25}
}
```

## Unity AI Tools by Aura

Aura offers 2 AI tools for Unity:
- **MCP for Unity** is available freely under the MIT license.
- **Aura for Unity** is a premium Unity/Unreal AI assistant built for game devs.

## Disclaimer

This project is a free and open-source tool for the Unity Editor, and is not affiliated with Unity Technologies.

---

**License:** MIT — see [LICENSE](LICENSE).
