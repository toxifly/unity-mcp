---
title: save_scene_scoped
sidebar_label: save_scene_scoped
description: "Save exactly one loaded Unity scene."
---

# `save_scene_scoped`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `core` &nbsp;·&nbsp; **Module:** `services.tools.scoped_asset_save`

## Description

Save exactly one loaded Unity scene. By default (unscoped_changes=include) the whole scene is saved. With unscoped_changes=exclude the scene is instead merged into the on-disk file at object-block granularity: new objects and objects mutated through ledger-instrumented MCP tools (component add/remove/set-property and the GameObject tools) are written; everything else — ambient drift (ExecuteAlways previews, layout-driven RectTransforms, TMP re-baking) but also Inspector, execute_code, and not-yet-instrumented MCP tool edits — is reported but NOT baked into the file.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `scene_path` | `str \| None` | — |  |
| `scene_name` | `str \| None` | — |  |
| `dirty_scene_policy` | `Literal['reject', 'preserve', 'allow']` | — |  |
| `unscoped_changes` | `Literal['exclude', 'include', 'reject']` | — |  |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->

