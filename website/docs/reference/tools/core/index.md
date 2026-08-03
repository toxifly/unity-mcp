---
title: "core tools"
sidebar_label: "core"
description: "MCP for Unity tools in the core group."
---

# `core` tools

Essential scene, script, asset & editor tools (always on by default)

- **[`apply_text_edits`](./apply_text_edits.md)** — Apply mutating, position-based text edits to a C# script identified by URI.
- **[`batch_execute`](./batch_execute.md)** — Execute multiple Unity MCP commands as one batch.
- **[`create_script`](./create_script.md)** — Create a new C# script at the given project path.
- **[`debug_request_context`](./debug_request_context.md)** — Return the current FastMCP request context details (client_id, session_id, and meta dump).
- **[`delete_script`](./delete_script.md)** — Delete a C# script by URI or Assets-relative path.
- **[`execute_custom_tool`](./execute_custom_tool.md)** — Execute a project-scoped custom tool by tool_name with optional parameters.
- **[`execute_menu_item`](./execute_menu_item.md)** — Execute the Unity menu item identified by menu_path.
- **[`find_gameobjects`](./find_gameobjects.md)** — Search for GameObjects in the scene by name, tag, layer, component type, or path.
- **[`find_in_file`](./find_in_file.md)** — Search a file identified by uri with a regular-expression pattern.
- **[`get_sha`](./get_sha.md)** — Get SHA256 and basic metadata for a Unity C# script without returning file contents.
- **[`inspect_provenance`](./inspect_provenance.md)** — Inspect compact scene and prefab provenance for hierarchy objects, assets, or GlobalObjectIds.
- **[`inspect_serialized`](./inspect_serialized.md)** — Inspect an explicit whitelist of Unity serialized properties using SerializedObject.
- **[`lifecycle_trace`](./lifecycle_trace.md)** — Start, poll, inspect, or stop a bounded opt-in trace of lifecycle callbacks, selection changes, and whitelisted serialized-property changes.
- **[`manage_asset`](./manage_asset.md)** — Manage Unity assets.
- **[`manage_build`](./manage_build.md)** — Manage Unity player builds — trigger builds, switch platforms, configure settings, manage build scenes and profiles, run batch builds across platforms.
- **[`manage_camera`](./manage_camera.md)** — Manage cameras (Unity Camera + Cinemachine).
- **[`manage_components`](./manage_components.md)** — Manage components on existing GameObjects.
- **[`manage_editor`](./manage_editor.md)** — Control and query Unity Editor state and settings.
- **[`manage_gameobject`](./manage_gameobject.md)** — Create, modify, delete, duplicate, move, or orient GameObjects.
- **[`manage_graphics`](./manage_graphics.md)** — Manage rendering graphics: volumes, post-processing, light baking, rendering stats, pipeline settings, and URP renderer features.
- **[`manage_material`](./manage_material.md)** — Manages Unity materials (set properties, colors, shaders, etc).
- **[`manage_packages`](./manage_packages.md)** — Manage Unity packages: query, install, remove, embed, and configure registries.
- **[`manage_physics`](./manage_physics.md)** — Manage physics settings, collision matrix, materials, joints, queries, and validation.
- **[`manage_prefabs`](./manage_prefabs.md)** — Manages Unity Prefab assets.
- **[`manage_scene`](./manage_scene.md)** — Performs CRUD operations on Unity scenes.
- **[`manage_script`](./manage_script.md)** — Compatibility router for legacy script operations.
- **[`manage_script_capabilities`](./manage_script_capabilities.md)** — Get manage_script capabilities (supported ops, limits, and guards).
- **[`manage_tools`](./manage_tools.md)** — Manage which tool groups are visible in this session. list_groups is read-only.
- **[`measure_ui`](./measure_ui.md)** — Read uGUI RectTransform bounds without mutating scene state.
- **[`preview_asset_changes`](./preview_asset_changes.md)** — Preview dirty objects for declared asset paths or one loaded scene without saving.
- **[`read_console`](./read_console.md)** — Gets messages from or clears the Unity Editor console.
- **[`refresh_unity`](./refresh_unity.md)** — Refresh Unity's asset database and optionally request script compilation.
- **[`save_assets_scoped`](./save_assets_scoped.md)** — Save only the declared Unity asset paths.
- **[`save_prefab_scoped`](./save_prefab_scoped.md)** — Save exactly one prefab asset with transaction-backed save-time change detection and rollback.
- **[`save_scene_scoped`](./save_scene_scoped.md)** — Save exactly one loaded Unity scene.
- **[`script_apply_edits`](./script_apply_edits.md)** — Structured C# edits (methods/classes) with safer boundaries - prefer this over raw text.
- **[`set_active_instance`](./set_active_instance.md)** — Set this client session's active Unity instance without changing Unity project state. instance accepts Name@hash, a unique hash prefix, or a port number in stdio mode; ambiguous identifiers are rejected.
- **[`validate_script`](./validate_script.md)** — Validate a C# script and return diagnostics.
