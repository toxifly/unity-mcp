---
title: manage_editor
sidebar_label: manage_editor
description: "Control and query Unity Editor state and settings."
---

# `manage_editor`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `core` &nbsp;·&nbsp; **Module:** `services.tools.manage_editor`

## Description

Control and query Unity Editor state and settings. Read-only actions: telemetry_status and telemetry_ping. Mutating actions: play, pause, stop, set_active_tool, add_tag, remove_tag, add_layer, remove_layer, deploy_package, restore_package, undo, and redo. deploy_package copies the configured MCPForUnity source into the installed package and triggers recompilation without a confirmation dialog; restore_package restores its backup. undo and redo return the affected group name.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `action` | `Literal['telemetry_status', 'telemetry_ping', 'play', 'pause', 'stop', 'set_active_tool', 'add_tag', 'remove_tag', 'add_layer', 'remove_layer', 'deploy_package', 'restore_package', 'undo', 'redo']` | yes | Editor action. deploy_package copies the configured MCPForUnity source into the project's package location and triggers recompilation; restore_package restores its backup; undo and redo apply Editor undo groups. |
| `tool_name` | `str \| None` | — | Tool name when setting active tool |
| `tag_name` | `str \| None` | — | Tag name when adding and removing tags |
| `layer_name` | `str \| None` | — | Layer name when adding and removing layers |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->

