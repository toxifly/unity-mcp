---
title: manage_profiler
sidebar_label: manage_profiler
description: "Control Unity Profiler sessions, read counters and frame timing, inspect object memory, capture or compare memory snapshots, and use the Frame Debugger. action supports profiler_start/stop/status/set_areas, get_frame_timing, get_counters…"
---

# `manage_profiler`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `profiling` &nbsp;·&nbsp; **Module:** `services.tools.manage_profiler`

## Description

Control Unity Profiler sessions, read counters and frame timing, inspect object memory, capture or compare memory snapshots, and use the Frame Debugger. action supports profiler_start/stop/status/set_areas, get_frame_timing, get_counters, get_object_memory, memory_take/list/compare_snapshots, and frame_debugger_enable/disable/get_events. The remaining parameters are action-specific; page_size and cursor page Frame Debugger events. Memory snapshots require com.unity.memoryprofiler and write .snap files; profiler_start may write a .raw recording. Session and Frame Debugger controls mutate transient Editor state.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `action` | `str` | yes | The profiler action to perform. |
| `category` | `str \| None` | — | Profiler category name for get_counters (e.g. Render, Scripts, Memory, Physics). |
| `counters` | `list[str] \| None` | — | Specific counter names for get_counters. Omit to read all in category. |
| `object_path` | `str \| None` | — | Scene hierarchy or asset path for get_object_memory. |
| `log_file` | `str \| None` | — | Path to .raw file for profiler_start recording. |
| `enable_callstacks` | `bool \| None` | — | Enable allocation callstacks for profiler_start. |
| `areas` | `dict[str, bool] \| None` | — | Dict of area name to bool for profiler_set_areas. |
| `snapshot_path` | `str \| None` | — | Output path for memory_take_snapshot. |
| `search_path` | `str \| None` | — | Search directory for memory_list_snapshots. |
| `snapshot_a` | `str \| None` | — | First snapshot path for memory_compare_snapshots. |
| `snapshot_b` | `str \| None` | — | Second snapshot path for memory_compare_snapshots. |
| `page_size` | `int \| None` | — | Page size for frame_debugger_get_events (default 50). |
| `cursor` | `int \| None` | — | Cursor offset for frame_debugger_get_events. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->

