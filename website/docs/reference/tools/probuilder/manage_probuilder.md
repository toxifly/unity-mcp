---
title: manage_probuilder
sidebar_label: manage_probuilder
description: "Create, query, and edit ProBuilder meshes; requires com.unity.probuilder. action covers shape creation (create_shape, create_poly_shape), face/edge editing (extrude, bevel, subdivide, delete, bridge, connect, detach, flip, merge, combine…"
---

# `manage_probuilder`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `probuilder` &nbsp;·&nbsp; **Module:** `services.tools.manage_probuilder`

## Description

Create, query, and edit ProBuilder meshes; requires com.unity.probuilder. action covers shape creation (create_shape, create_poly_shape), face/edge editing (extrude, bevel, subdivide, delete, bridge, connect, detach, flip, merge, combine, duplicate, create_polygon), vertex editing (merge, weld, split, move, insert, append), select_faces, face material/color/UV assignment, get_mesh_info, convert_to_probuilder, smoothing, pivot changes, freeze_transform, validate_mesh, and repair_mesh. target and search_method identify the object; properties carries action-specific indices, edges, vectors, shape settings, and materials. get_mesh_info and validate_mesh are read-only; every other action mutates scene objects. get_mesh_info include accepts summary, faces, edges, or all.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `action` | `str` | yes | Action to perform. |
| `target` | `str \| None` | — | Target GameObject (name/path/id). |
| `search_method` | `Literal['by_id', 'by_name', 'by_path', 'by_tag', 'by_layer'] \| None` | — | How to find the target GameObject. |
| `properties` | `dict[str, Any] \| str \| None` | — | Action-specific parameters (dict or JSON string). |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->

