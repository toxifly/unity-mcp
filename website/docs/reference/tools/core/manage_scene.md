---
title: manage_scene
sidebar_label: manage_scene
description: "Performs CRUD operations on Unity scenes."
---

# `manage_scene`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `core` &nbsp;·&nbsp; **Module:** `services.tools.manage_scene`

## Description

Performs CRUD operations on Unity scenes. Read-only actions: get_hierarchy, get_active, get_build_settings, get_loaded_scenes, scene_view_frame. Modifying actions: create (with optional template), load (with optional additive flag), save, close_scene, set_active_scene, move_to_scene, apply_external_edit, and validate when auto_repair is enabled. apply_external_edit rewrites a .unity file on disk and resyncs the Editor in one step, which is the only safe way to hand-patch scene YAML: doing it outside Unity raises a modal reload prompt that blocks the Editor's main thread and hangs this bridge until a human clicks it. get_hierarchy supports page_size, cursor, max_depth, and include_transform.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `action` | `Literal['create', 'load', 'save', 'get_hierarchy', 'get_active', 'get_build_settings', 'scene_view_frame', 'close_scene', 'set_active_scene', 'get_loaded_scenes', 'move_to_scene', 'apply_external_edit', 'validate']` | yes | Perform CRUD operations on Unity scenes and control the Scene View camera. |
| `name` | `str \| None` | — | Scene name. |
| `path` | `str \| None` | — | Scene path. |
| `build_index` | `int \| str \| None` | — | Unity build index (quote as string, e.g., '0'). |
| `scene_view_target` | `str \| int \| None` | — | GameObject reference for scene_view_frame (name, path, or instance ID). |
| `parent` | `str \| int \| None` | — | Optional parent GameObject reference (name/path/instanceID) to list direct children. |
| `page_size` | `int \| str \| None` | — | Page size for get_hierarchy paging. |
| `cursor` | `int \| str \| None` | — | Opaque cursor for paging (offset). |
| `max_nodes` | `int \| str \| None` | — | Hard cap on returned nodes per request (safety). |
| `max_depth` | `int \| str \| None` | — | Accepted for forward-compatibility; current paging returns a single level. |
| `max_children_per_node` | `int \| str \| None` | — | Child paging hint (safety). |
| `include_transform` | `bool \| str \| None` | — | If true, include local transform in node summaries. |
| `scene_name` | `str \| None` | — | Scene name for multi-scene operations. |
| `scene_path` | `str \| None` | — | Full scene path (e.g. 'Assets/Scenes/Level2.unity'). |
| `target` | `str \| int \| None` | — | GameObject reference (name, path, or instanceID) for move_to_scene. |
| `remove_scene` | `bool \| str \| None` | — | For close_scene: true to fully remove, false to just unload. |
| `additive` | `bool \| str \| None` | — | For load: true to open scene additively (keeps current scene). |
| `template` | `str \| None` | — | For create: scene template ('empty', 'default', '3d_basic', '2d_basic'). Omit for empty scene. |
| `auto_repair` | `bool \| str \| None` | — | For validate: true to auto-fix missing scripts (undoable). |
| `edits` | `list[dict[str, Any]] \| None` | — | For apply_external_edit: literal replacements applied to the scene file, each {'old_text': ..., 'new_text': ..., 'count': 1}. Every anchor must match exactly 'count' times or nothing is written. Omit for a pure discard-and-reload of a file already changed on disk. |
| `discard_unsaved` | `bool \| str \| None` | — | For apply_external_edit: true to drop the open scene's unsaved in-memory changes, which the rewrite would otherwise refuse to discard. |
| `dry_run` | `bool \| str \| None` | — | For apply_external_edit: true to report which anchors matched without writing. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
### Load a scene from `Assets/Scenes/`

> Open `Assets/Scenes/MainMenu.unity`.

```json
{
  "action": "load",
  "path": "Scenes/MainMenu.unity"
}
```

Paths are relative to `Assets/`. Forward slashes only.

### Get the scene hierarchy (paged)

> List every GameObject in the active scene.

```json
{
  "action": "get_hierarchy",
  "page_size": 100
}
```

Returns up to `page_size` entries plus a `next_cursor` for the remainder. Always page large hierarchies.

### Save the active scene

> Save the active scene under its existing path.

```json
{ "action": "save" }
```

### Create a scene from a template

> Make a new 3D scene called `Lab`.

```json
{
  "action": "create",
  "path": "Scenes/Lab.unity",
  "template": "3d_basic"
}
```

Other templates: `2d_basic`, `default`, `empty`.

### Additive multi-scene editing

> Load `Scenes/Boss.unity` additively while keeping the current scene open.

```json
{
  "action": "load",
  "path": "Scenes/Boss.unity",
  "additive": true
}
```

Use `set_active_scene`, `close_scene`, and `move_to_scene` to compose multi-scene setups.

### Hand-patch a scene file without wedging the Editor

> Editing a `.unity` file on disk while that scene is open makes Unity raise a modal
> "reload scene?" prompt. That dialog runs a nested message loop on the Editor's main thread —
> the same thread this bridge pumps on — so every later call times out until a human clicks it.
> `apply_external_edit` writes and resyncs in one main-thread step, closing the scene before the
> file changes, so the prompt has nothing to ask about.

```json
{
  "action": "apply_external_edit",
  "path": "Assets/Scenes/Main.unity",
  "edits": [
    {"old_text": "propertyPath: m_Enabled
      value: 0", "new_text": "propertyPath: m_Enabled
      value: 1"}
  ],
  "discard_unsaved": true
}
```

Every anchor must match exactly `count` times (default 1) or nothing is written, so a moved anchor
fails the call instead of half-migrating the file. Add `"dry_run": true` to check the anchors first.
Omitting `edits` is a plain discard-and-reload, which is the rescue path when the file was already
changed by something else.
<!-- examples:end -->

