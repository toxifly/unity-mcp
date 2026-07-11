---
title: manage_components
sidebar_label: manage_components
description: "Manage components on existing GameObjects."
---

# `manage_components`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `core` &nbsp;·&nbsp; **Module:** `services.tools.manage_components`

## Description

Manage components on existing GameObjects. Read-only actions: get_properties and get_components. Mutating actions: add, remove, and set_property. Targets accept a GameObject name, hierarchy path, instance ID, or structured target; property filters can bound serialized-property reads.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `action` | `Literal['add', 'remove', 'set_property', 'get_properties', 'get_components']` | yes | Action to perform: add, remove, set_property, get_properties, get_components |
| `target` | `dict[str, Any] \| str \| int` | yes | Target GameObject reference - instance ID, name, path, or object like {"instanceID": 123} / {"name": "Player"} / {"path": "/Canvas/Panel"} |
| `component_type` | `str \| None` | — | Component type name (e.g., 'Rigidbody', 'BoxCollider', 'MyScript') |
| `search_method` | `Literal['by_id', 'by_name', 'by_path'] \| None` | — | How to find the target GameObject |
| `property` | `str \| None` | — | Property name to set (for set_property action) |
| `value` | `str \| int \| float \| bool \| dict[Any] \| list[Any] \| None` | — | Value to set (for set_property action). For object references: instance ID (int), asset path (string), or {"guid": "..."} / {"path": "..."}. For Sprite sub-assets: {"guid": "...", "spriteName": "<name>"} or {"guid": "...", "fileID": <id>}. Single-sprite textures auto-resolve. |
| `properties` | `dict[str, Any] \| str \| None` | — | Dictionary of property names to values. Example: {"mass": 5.0, "useGravity": false} |
| `component_index` | `int \| None` | — | Zero-based index to select which component when multiple of the same type exist. Use the components resource to discover indices. If omitted, targets the first instance. |
| `property_names` | `list[str] \| str \| None` | — | For get_properties: list of property/field names to read. Accepts a JSON array string or a comma-separated string. |
| `page_size` | `int \| str \| None` | — | For get_components: page size (default 25). |
| `cursor` | `int \| str \| None` | — | For get_components: pagination cursor (default 0). |
| `include_properties` | `bool \| str \| None` | — | For get_components: include serialized component properties (default false). |
| `property_whitelist` | `list[str] \| None` | — | For get_components: only return these property names (requires include_properties=true). |
| `property_blacklist` | `list[str] \| None` | — | For get_components: exclude these property names from results. |
| `change_guard` | `dict[str, Any] \| str \| None` | — | Reject and roll back mutations outside expected_objects/expected_properties or max_changed_objects. |
| `dry_run` | `bool \| str \| None` | — | Preview the exact serialized changes, then roll them back without saving or changing dirty state. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->

