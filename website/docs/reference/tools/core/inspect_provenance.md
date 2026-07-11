---
title: inspect_provenance
sidebar_label: inspect_provenance
description: "Inspect compact scene and prefab provenance for hierarchy objects, assets, or GlobalObjectIds."
---

# `inspect_provenance`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `core` &nbsp;·&nbsp; **Module:** `services.tools.inspect_provenance`

## Description

Inspect compact scene and prefab provenance for hierarchy objects, assets, or GlobalObjectIds. Reports stable identity, scene/asset paths, instance roots, source prefab objects, property override paths, and added/removed components without dumping serialized component values.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `targets` | `list[str] \| str` | yes |  |
| `include_property_overrides` | `bool` | — |  |
| `include_component_overrides` | `bool` | — |  |
| `include_inactive` | `bool` | — |  |
| `override_limit` | `int` | — |  |
| `component_limit` | `int` | — |  |
| `page_size` | `int` | — |  |
| `cursor` | `str \| None` | — |  |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->

