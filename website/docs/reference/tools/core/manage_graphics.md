---
title: manage_graphics
sidebar_label: manage_graphics
description: "Manage volumes and post-processing, light baking, rendering statistics, render-pipeline settings, URP renderer features, and skybox/environment settings. action prefixes select the domain: volume_*, bake_*, stats_*, pipeline_*, feature_*…"
---

# `manage_graphics`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `core` &nbsp;·&nbsp; **Module:** `services.tools.manage_graphics`

## Description

Manage volumes and post-processing, light baking, rendering statistics, render-pipeline settings, URP renderer features, and skybox/environment settings. action prefixes select the domain: volume_*, bake_*, stats_*, pipeline_*, feature_*, or skybox_*; ping reports the active pipeline and supported features. target, effect, parameters, properties, settings, and the remaining domain-specific fields supply action inputs. Volume actions require URP or HDRP, renderer-feature actions require URP, and baking runs only in Edit mode. get/list/status actions are read-only; create, set, add, remove, configure, toggle, reorder, bake, and clear actions mutate scene assets or project rendering settings.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `action` | `str` | yes | The graphics action to perform. |
| `target` | `str \| None` | — | Target object name or instance ID. |
| `effect` | `str \| None` | — | Effect type name (e.g., 'Bloom', 'Vignette'). |
| `parameters` | `dict[str, Any] \| None` | — | Dict of parameter values. |
| `properties` | `dict[str, Any] \| None` | — | Dict of properties to set. |
| `settings` | `dict[str, Any] \| None` | — | Dict of settings (bake/pipeline). |
| `name` | `str \| None` | — | Name for created objects. |
| `is_global` | `bool \| None` | — | Whether Volume is global (default true). |
| `weight` | `float \| None` | — | Volume weight (0-1). |
| `priority` | `float \| None` | — | Volume priority. |
| `profile_path` | `str \| None` | — | Asset path for VolumeProfile. |
| `effects` | `list[dict[str, Any]] \| None` | — | Effect definitions for volume_create. |
| `path` | `str \| None` | — | Asset path for volume_create_profile. |
| `level` | `str \| None` | — | Quality level name or index. |
| `position` | `list[float] \| None` | — | Position [x,y,z]. |
| `grid_size` | `list[int] \| None` | — | Probe grid size [x,y,z]. |
| `spacing` | `float \| None` | — | Probe grid spacing. |
| `size` | `list[float] \| None` | — | Probe/volume size [x,y,z]. |
| `resolution` | `int \| None` | — | Probe resolution. |
| `mode` | `str \| None` | — | Probe mode or debug mode. |
| `hdr` | `bool \| None` | — | HDR for reflection probes. |
| `box_projection` | `bool \| None` | — | Box projection for reflection probes. |
| `positions` | `list[list[float]] \| None` | — | Probe positions array. |
| `index` | `int \| None` | — | Feature index. |
| `active` | `bool \| None` | — | Feature active state. |
| `order` | `list[int] \| None` | — | Feature reorder indices. |
| `async_bake` | `bool \| None` | — | Async bake (default true). |
| `feature_type` | `str \| None` | — | Renderer feature type name. |
| `material` | `str \| None` | — | Material asset path for feature. |
| `color` | `list[float] \| None` | — | Color [r,g,b,a] for ambient/fog. |
| `intensity` | `float \| None` | — | Intensity value (ambient/reflection). |
| `ambient_mode` | `str \| None` | — | Ambient mode: Skybox, Trilight, Flat, Custom. |
| `equator_color` | `list[float] \| None` | — | Equator color [r,g,b,a] (Trilight mode). |
| `ground_color` | `list[float] \| None` | — | Ground color [r,g,b,a] (Trilight mode). |
| `fog_enabled` | `bool \| None` | — | Enable or disable fog. |
| `fog_mode` | `str \| None` | — | Fog mode: Linear, Exponential, ExponentialSquared. |
| `fog_color` | `list[float] \| None` | — | Fog color [r,g,b,a]. |
| `fog_density` | `float \| None` | — | Fog density (Exponential modes). |
| `fog_start` | `float \| None` | — | Fog start distance (Linear mode). |
| `fog_end` | `float \| None` | — | Fog end distance (Linear mode). |
| `bounces` | `int \| None` | — | Reflection bounces. |
| `reflection_mode` | `str \| None` | — | Default reflection mode: Skybox, Custom. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->

