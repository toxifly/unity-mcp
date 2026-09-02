---
title: manage_editor
sidebar_label: manage_editor
description: "Control and query Unity Editor state and settings."
---

# `manage_editor`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `core` &nbsp;·&nbsp; **Module:** `services.tools.manage_editor`

## Description

Control and query Unity Editor state and settings. Read-only actions: telemetry_status, telemetry_ping, and get_scripting_defines. Mutating actions: play, pause, stop, set_active_tool, add_tag, remove_tag, add_layer, remove_layer, set_scripting_defines, deploy_package, restore_package, undo, and redo. set_scripting_defines replaces the whole symbol list for a build target (pass [] to clear) and triggers a recompile. deploy_package copies the configured MCPForUnity source into the installed package and triggers recompilation without a confirmation dialog; restore_package restores its backup. undo and redo return the affected group name.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `action` | `Literal['telemetry_status', 'telemetry_ping', 'play', 'pause', 'stop', 'set_active_tool', 'add_tag', 'remove_tag', 'add_layer', 'remove_layer', 'get_scripting_defines', 'set_scripting_defines', 'deploy_package', 'restore_package', 'undo', 'redo']` | yes | Editor action. deploy_package copies the configured MCPForUnity source into the project's package location and triggers recompilation; restore_package restores its backup; undo and redo apply Editor undo groups. |
| `tool_name` | `str \| None` | — | Tool name when setting active tool |
| `tag_name` | `str \| None` | — | Tag name when adding and removing tags |
| `layer_name` | `str \| None` | — | Layer name when adding and removing layers |
| `defines` | `list[str] \| str \| None` | — | Full scripting define symbol list for set_scripting_defines; [] clears them |
| `target` | `str \| None` | — | Build target for scripting defines (e.g. windows64, android); defaults to the active one |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->

