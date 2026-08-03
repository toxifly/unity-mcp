---
title: Tool reference
sidebar_label: Tools
sidebar_class_name: sidebar-hidden
slug: /reference/tools
description: Auto-generated catalog of every MCP for Unity tool, grouped by domain.
---

# Tool reference

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

Every tool MCP for Unity exposes, generated directly from the Python `@mcp_for_unity_tool` registry under `Server/src/services/tools/`.

## `animation` &nbsp; (1 tool)
Animator control & AnimationClip creation
- **[`manage_animation`](./animation/manage_animation.md)** — Manage Unity animation: Animator control and AnimationClip creation.

## `asset_gen` &nbsp; (5 tools)
AI asset generation – 3D model gen/import, 2D image gen & audio gen (bring-your-own-key)
- **[`generate_audio`](./asset_gen/generate_audio.md)** — Generate audio (sound effects and background music) with fal.ai models and import them as AudioClips into the Unity project.
- **[`generate_image`](./asset_gen/generate_image.md)** — Generate 2D images with AI providers (fal.ai, OpenRouter) and import them as textures/sprites into the Unity project.
- **[`generate_model`](./asset_gen/generate_model.md)** — Generate 3D models with AI providers (Tripo, Meshy) and import them into the Unity project.
- **[`import_model`](./asset_gen/import_model.md)** — Import 3D models from the Sketchfab marketplace into the Unity project.
- **[`import_model_file`](./asset_gen/import_model_file.md)** — Import a local 3D model file that already exists on disk (e.g. an FBX/OBJ/glTF exported from Blender or another DCC tool) into the Unity project.

## `core` &nbsp; (38 tools)
Essential scene, script, asset & editor tools (always on by default)
- **[`apply_text_edits`](./core/apply_text_edits.md)** — Apply mutating, position-based text edits to a C# script identified by URI.
- **[`batch_execute`](./core/batch_execute.md)** — Execute multiple Unity MCP commands as one batch.
- **[`create_script`](./core/create_script.md)** — Create a new C# script at the given project path.
- **[`debug_request_context`](./core/debug_request_context.md)** — Return the current FastMCP request context details (client_id, session_id, and meta dump).
- **[`delete_script`](./core/delete_script.md)** — Delete a C# script by URI or Assets-relative path.
- **[`execute_custom_tool`](./core/execute_custom_tool.md)** — Execute a project-scoped custom tool by tool_name with optional parameters.
- **[`execute_menu_item`](./core/execute_menu_item.md)** — Execute the Unity menu item identified by menu_path.
- **[`find_gameobjects`](./core/find_gameobjects.md)** — Search for GameObjects in the scene by name, tag, layer, component type, or path.
- **[`find_in_file`](./core/find_in_file.md)** — Search a file identified by uri with a regular-expression pattern.
- **[`get_sha`](./core/get_sha.md)** — Get SHA256 and basic metadata for a Unity C# script without returning file contents.
- **[`inspect_provenance`](./core/inspect_provenance.md)** — Inspect compact scene and prefab provenance for hierarchy objects, assets, or GlobalObjectIds.
- **[`inspect_serialized`](./core/inspect_serialized.md)** — Inspect an explicit whitelist of Unity serialized properties using SerializedObject.
- **[`lifecycle_trace`](./core/lifecycle_trace.md)** — Start, poll, inspect, or stop a bounded opt-in trace of lifecycle callbacks, selection changes, and whitelisted serialized-property changes.
- **[`manage_asset`](./core/manage_asset.md)** — Manage Unity assets.
- **[`manage_build`](./core/manage_build.md)** — Manage Unity player builds — trigger builds, switch platforms, configure settings, manage build scenes and profiles, run batch builds across platforms.
- **[`manage_camera`](./core/manage_camera.md)** — Manage cameras (Unity Camera + Cinemachine).
- **[`manage_components`](./core/manage_components.md)** — Manage components on existing GameObjects.
- **[`manage_editor`](./core/manage_editor.md)** — Control and query Unity Editor state and settings.
- **[`manage_gameobject`](./core/manage_gameobject.md)** — Create, modify, delete, duplicate, move, or orient GameObjects.
- **[`manage_graphics`](./core/manage_graphics.md)** — Manage rendering graphics: volumes, post-processing, light baking, rendering stats, pipeline settings, and URP renderer features.
- **[`manage_material`](./core/manage_material.md)** — Manages Unity materials (set properties, colors, shaders, etc).
- **[`manage_packages`](./core/manage_packages.md)** — Manage Unity packages: query, install, remove, embed, and configure registries.
- **[`manage_physics`](./core/manage_physics.md)** — Manage physics settings, collision matrix, materials, joints, queries, and validation.
- **[`manage_prefabs`](./core/manage_prefabs.md)** — Manages Unity Prefab assets.
- **[`manage_scene`](./core/manage_scene.md)** — Performs CRUD operations on Unity scenes.
- **[`manage_script`](./core/manage_script.md)** — Compatibility router for legacy script operations.
- **[`manage_script_capabilities`](./core/manage_script_capabilities.md)** — Get manage_script capabilities (supported ops, limits, and guards).
- **[`manage_tools`](./core/manage_tools.md)** — Manage which tool groups are visible in this session. list_groups is read-only.
- **[`measure_ui`](./core/measure_ui.md)** — Read uGUI RectTransform bounds without mutating scene state.
- **[`preview_asset_changes`](./core/preview_asset_changes.md)** — Preview dirty objects for declared asset paths or one loaded scene without saving.
- **[`read_console`](./core/read_console.md)** — Gets messages from or clears the Unity Editor console.
- **[`refresh_unity`](./core/refresh_unity.md)** — Refresh Unity's asset database and optionally request script compilation.
- **[`save_assets_scoped`](./core/save_assets_scoped.md)** — Save only the declared Unity asset paths.
- **[`save_prefab_scoped`](./core/save_prefab_scoped.md)** — Save exactly one prefab asset with transaction-backed save-time change detection and rollback.
- **[`save_scene_scoped`](./core/save_scene_scoped.md)** — Save exactly one loaded Unity scene.
- **[`script_apply_edits`](./core/script_apply_edits.md)** — Structured C# edits (methods/classes) with safer boundaries - prefer this over raw text.
- **[`set_active_instance`](./core/set_active_instance.md)** — Set this client session's active Unity instance without changing Unity project state. instance accepts Name@hash, a unique hash prefix, or a port number in stdio mode; ambiguous identifiers are rejected.
- **[`validate_script`](./core/validate_script.md)** — Validate a C# script and return diagnostics.

## `docs` &nbsp; (2 tools)
Unity API reflection and documentation lookup
- **[`unity_docs`](./docs/unity_docs.md)** — Fetch official Unity documentation from docs.unity3d.com.
- **[`unity_reflect`](./docs/unity_reflect.md)** — Inspect Unity's live C# API via reflection without mutating editor or project state.

## `probuilder` &nbsp; (1 tool)
ProBuilder 3D modeling – requires com.unity.probuilder package
- **[`manage_probuilder`](./probuilder/manage_probuilder.md)** — Manage ProBuilder meshes for in-editor 3D modeling.

## `profiling` &nbsp; (1 tool)
Unity Profiler session control, counters, memory snapshots & Frame Debugger
- **[`manage_profiler`](./profiling/manage_profiler.md)** — Unity Profiler session control, counter reads, memory snapshots, and Frame Debugger.

## `scripting_ext` &nbsp; (2 tools)
ScriptableObject management
- **[`execute_code`](./scripting_ext/execute_code.md)** — Execute arbitrary C# code inside the Unity Editor.
- **[`manage_scriptable_object`](./scripting_ext/manage_scriptable_object.md)** — Create or modify ScriptableObject assets using Unity SerializedObject property paths.

## `testing` &nbsp; (2 tools)
Test runner & async test jobs
- **[`get_test_job`](./testing/get_test_job.md)** — Read the status or results of an asynchronous Unity test job by job_id.
- **[`run_tests`](./testing/run_tests.md)** — Start an asynchronous Unity Test Framework run and return a job_id immediately.

## `ui` &nbsp; (1 tool)
UI Toolkit (UXML, USS, UIDocument)
- **[`manage_ui`](./ui/manage_ui.md)** — Manages Unity UI Toolkit elements (UXML documents, USS stylesheets, UIDocument components).

## `vfx` &nbsp; (3 tools)
Visual effects – VFX Graph, shaders, procedural textures
- **[`manage_shader`](./vfx/manage_shader.md)** — Manages shader scripts in Unity (create, read, update, delete).
- **[`manage_texture`](./vfx/manage_texture.md)** — Procedural texture generation for Unity.
- **[`manage_vfx`](./vfx/manage_vfx.md)** — Manage Unity VFX components (ParticleSystem, VisualEffect, LineRenderer, TrailRenderer).

