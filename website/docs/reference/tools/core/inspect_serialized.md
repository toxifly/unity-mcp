---
title: inspect_serialized
sidebar_label: inspect_serialized
description: "Inspect an explicit whitelist of Unity serialized properties using SerializedObject."
---

# `inspect_serialized`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `core` &nbsp;·&nbsp; **Module:** `services.tools.inspect_serialized`

## Description

Inspect an explicit whitelist of Unity serialized properties using SerializedObject. Targets may be hierarchy names/paths, GlobalObjectIds, or objects with 'target' and an optional 'component' type filter. Reports null and broken object references distinctly and can include compact prefab source/override provenance. Set prefab_path to scope name/path/GlobalObjectId targets to an Assets/ or Packages/ prefab. A matching open Prefab Stage is reused so unsaved edits are inspected.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `targets` | `list[str \| dict[str, str]] \| str` | yes |  |
| `properties` | `list[str] \| str` | yes |  |
| `component_type` | `str \| None` | — |  |
| `include_prefab_provenance` | `bool` | — |  |
| `include_missing_references` | `bool` | — |  |
| `include_inactive` | `bool` | — |  |
| `page_size` | `int` | — |  |
| `cursor` | `str \| None` | — |  |
| `prefab_path` | `str \| None` | — |  |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
```python
inspect_serialized(
    prefab_path="Assets/Prefabs/Enemy.prefab",
    targets=[{"target": "Enemy", "component": "EnemyController"}],
    properties=["movementSpeed", "weapon"],
)
```
<!-- examples:end -->

