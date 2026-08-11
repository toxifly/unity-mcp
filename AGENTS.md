# AGENTS.md

These repository instructions apply to every agent and all subdirectories.

## Mandatory Server-Version Invariant

The Unity package and Python server are released and validated as one exact versioned unit.

- Default client and server configuration must use `mcpforunityserver==<exact-version>` derived from the installed Unity package version.
- Normalize SemVer prereleases to their exact PEP 440 spelling: for example, `10.1.1-beta.1` must become `10.1.1b1`.
- Never use an unpinned package name, version range (`>=`, `~=`, wildcard, or similar), prerelease channel, `latest` lookup, or compatibility fallback that can select another server version.
- If the local package version is missing, unknown, invalid, cannot be normalized, or the exact server release is unavailable, stop with an actionable error. Never silently download, select, or add a different version.
- An explicit local **Server Source Override** pointing to `Server/` is permitted for development. It represents a deliberate user choice and must never be synthesized as a fallback.
- Keep `MCPForUnity/package.json`, root `manifest.json`, `Server/pyproject.toml`, and `Server/uv.lock` version-aligned.
- Any change to package-source generation or version compatibility must retain or add tests proving exact stable and prerelease pins and proving that broad or unpinned fallbacks are absent.

Do not weaken this invariant to preserve backward compatibility. The mandatory bridge handshake is expected to reject mismatched versions.
