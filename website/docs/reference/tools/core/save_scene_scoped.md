---
title: save_scene_scoped
sidebar_label: save_scene_scoped
description: "Save exactly one loaded Unity scene through the mutation transaction boundary."
---

# `save_scene_scoped`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `core` &nbsp;·&nbsp; **Module:** `services.tools.scoped_asset_save`

## Description

Save exactly one loaded Unity scene through the mutation transaction boundary. The save rolls back if serialization introduces property changes beyond the pre-save state.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `scene_path` | `str \| None` | — |  |
| `scene_name` | `str \| None` | — |  |
| `dirty_scene_policy` | `Literal['reject', 'preserve', 'allow']` | — |  |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->

