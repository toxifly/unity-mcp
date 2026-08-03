---
title: measure_ui
sidebar_label: measure_ui
description: "Read uGUI RectTransform bounds without mutating scene state."
---

# `measure_ui`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `core` &nbsp;·&nbsp; **Module:** `services.tools.measure_ui`

## Description

Read uGUI RectTransform bounds without mutating scene state. Returns each target's rectangle in an explicit canvas, local, world, or screen-pixel coordinate space. Targets are GameObject names, or hierarchy paths ('Canvas/Panel/Button') for disambiguation. Set prefab_path to inspect a prefab asset headlessly instead of the loaded scene; a matching open Prefab Stage is reused so unsaved edits are visible. Prefab layout groups are rebuilt before measurement, canvas space falls back to the prefab's root RectTransform, and screen_pixels/on_screen/not_clipped are unavailable for prefab assets. Set include_children to also measure each target's immediate RectTransform children. Inactive objects are included by default. Geometry assertions can be evaluated in the same call.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `targets` | `list[str] \| str \| None` | — |  |
| `container` | `str \| None` | — |  |
| `reference` | `str \| None` | — |  |
| `include_children` | `bool \| str \| None` | — |  |
| `include_inactive` | `bool \| str \| None` | — |  |
| `space` | `Literal['canvas', 'local', 'world', 'screen_pixels']` | — |  |
| `assertions` | `list[dict[str, Any]] \| None` | — |  |
| `prefab_path` | `str \| None` | — |  |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
```python
measure_ui(
    prefab_path="Assets/UI/HUD.prefab",
    targets=["HUD/HealthBar"],
    space="canvas",
)
```
<!-- examples:end -->

