---
title: manage_prefabs
sidebar_label: manage_prefabs
description: "Manages Unity Prefab assets."
---

# `manage_prefabs`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `core` &nbsp;·&nbsp; **Module:** `services.tools.manage_prefabs`

## Description

Manages Unity Prefab assets. Read-only actions: get_info and get_hierarchy. Mutating actions: create_from_gameobject, create_and_replace, modify_contents, apply_instance_overrides, revert_instance_overrides, unpack_instance, open_prefab_stage, save_prefab_stage, close_prefab_stage. modify_contents edits an asset without opening Prefab Stage; open_prefab_stage, save_prefab_stage, and close_prefab_stage control interactive stage state. create_and_replace performs guarded prefab creation with optional scene replacement. With modify_contents, create_child and delete_child accept one item or an array, and component_properties sets serialized fields on existing components. Object references accept guid, Assets path, or instanceID forms.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `action` | `Literal['create_from_gameobject', 'create_and_replace', 'get_info', 'get_hierarchy', 'modify_contents', 'apply_instance_overrides', 'revert_instance_overrides', 'unpack_instance', 'open_prefab_stage', 'save_prefab_stage', 'close_prefab_stage']` | yes | Prefab operation to perform. |
| `prefab_path` | `str \| None` | — | Prefab asset path (e.g., Assets/Prefabs/MyPrefab.prefab). |
| `target` | `dict[str, Any] \| str \| int \| None` | — | Target GameObject reference. Accepts instance ID, name, path, or object like {"instanceID": 123} / {"name": "Player"} / {"path": "/Canvas/Panel"}. |
| `search_method` | `Literal['by_id', 'by_name', 'by_path'] \| None` | — | How to resolve the target (optional). |
| `allow_overwrite` | `bool \| str \| None` | — | Allow replacing existing prefab. |
| `search_inactive` | `bool \| str \| None` | — | Include inactive GameObjects in search. |
| `unlink_if_instance` | `bool \| None` | — | Unlink from existing prefab before creating new one. |
| `preserve_world_transform` | `bool \| str \| None` | — | For create_and_replace, preserve and validate the source hierarchy's world transform. |
| `preserve_scene_references` | `bool \| str \| None` | — | For create_and_replace, validate that external scene references into the hierarchy remain intact. |
| `save_scene` | `bool \| str \| None` | — | For create_and_replace, save the owning scene after linking the prefab instance. |
| `link_scene_instance` | `bool \| str \| None` | — | For create_and_replace, connect the source hierarchy to the new prefab; false creates only the asset. |
| `dirty_scene_policy` | `Literal['reject', 'preserve', 'allow'] \| None` | — | Dirty-scene policy for create_and_replace. Defaults to reject; dry runs preserve prior dirtiness. |
| `change_guard` | `dict[str, Any] \| None` | — | Expected serialized change scope for create_and_replace. |
| `dry_run` | `bool \| str \| None` | — | Preview create_and_replace changes and roll back the scene and prefab asset. |
| `unpack_mode` | `str \| None` | — | For unpack_instance: unpack mode. Valid values: OutermostRoot, Completely. |
| `position` | `list[float] \| dict[str, float] \| str \| None` | — | New local position [x, y, z] or {x, y, z} for modify_contents. |
| `rotation` | `list[float] \| dict[str, float] \| str \| None` | — | New local rotation (euler angles) [x, y, z] or {x, y, z} for modify_contents. |
| `scale` | `list[float] \| dict[str, float] \| str \| None` | — | New local scale [x, y, z] or {x, y, z} for modify_contents. |
| `name` | `str \| None` | — | New name for the target object in modify_contents. |
| `tag` | `str \| None` | — | New tag for the target object in modify_contents. |
| `layer` | `str \| None` | — | New layer name for the target object in modify_contents. |
| `set_active` | `bool \| None` | — | Set active state of target object in modify_contents. |
| `parent` | `str \| None` | — | New parent object name/path within prefab for modify_contents. |
| `components_to_add` | `list[str] \| None` | — | Component types to add in modify_contents. |
| `components_to_remove` | `list[str] \| None` | — | Component types to remove in modify_contents. |
| `create_child` | `dict[str, Any] \| list[dict[str, Any]] \| None` | — | Create child GameObject(s) in the prefab. Single object or array of objects, each with: name (required), parent (optional, defaults to target), source_prefab_path (optional: asset path to instantiate as nested prefab, e.g. 'Assets/Prefabs/Bullet.prefab'), primitive_type (optional: Cube, Sphere, Capsule, Cylinder, Plane, Quad), position, rotation, scale, components_to_add, tag, layer, set_active. source_prefab_path and primitive_type are mutually exclusive. |
| `delete_child` | `str \| list[str] \| None` | — | Child name(s) or path(s) to remove from the prefab. Supports single string or array for batch deletion (e.g. 'Child1' or ['Child1', 'Child1/Grandchild']). |
| `component_properties` | `dict[str, dict[str, Any]] \| None` | — | Set properties on existing components in modify_contents. Keys are component type names, values are dicts of property name to value. Example: {"Rigidbody": {"mass": 5.0}, "MyScript": {"health": 100}}. Supports object references via {"guid": "..."}, {"path": "Assets/..."}, or {"instanceID": 123}. For Sprite sub-assets: {"guid": "...", "spriteName": "<name>"}. Single-sprite textures auto-resolve. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->

