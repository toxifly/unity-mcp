---
title: lifecycle_trace
sidebar_label: lifecycle_trace
description: "Start, poll, inspect, or stop a bounded opt-in trace of lifecycle callbacks, selection changes, and whitelisted serialized-property changes."
---

# `lifecycle_trace`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `core` &nbsp;·&nbsp; **Module:** `services.tools.lifecycle_trace`

## Description

Start, poll, inspect, or stop a bounded opt-in trace of lifecycle callbacks, selection changes, and whitelisted serialized-property changes. Temporary instrumentation is removed on stop, timeout, play-mode exit, or domain reload.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `action` | `Literal['start', 'poll', 'status', 'stop']` | yes |  |
| `session_id` | `str \| None` | — |  |
| `targets` | `list[str] \| str \| None` | — |  |
| `events` | `list[Literal['Awake', 'OnEnable', 'OnDisable', 'OnDestroy', 'serialized_property_change', 'selection_change']] \| None` | — |  |
| `property_whitelist` | `list[str] \| None` | — |  |
| `max_events` | `int` | — |  |
| `timeout_seconds` | `int` | — |  |
| `cursor` | `int` | — |  |
| `limit` | `int` | — |  |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->

