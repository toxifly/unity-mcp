---
title: script_apply_edits
sidebar_label: script_apply_edits
description: "Apply mutating, structured C# method or anchor edits with boundary validation. edits accepts a list or JSON string using replace_method, insert_method, delete_method, anchor_insert, anchor_delete, or anchor_replace."
---

# `script_apply_edits`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `core` &nbsp;·&nbsp; **Module:** `services.tools.script_apply_edits`

## Description

Apply mutating, structured C# method or anchor edits with boundary validation. edits accepts a list or JSON string using replace_method, insert_method, delete_method, anchor_insert, anchor_delete, or anchor_replace. Method operations use className, methodName, replacement, position, afterMethodName, and beforeMethodName as applicable; anchor operations use anchor and text. options controls validation and refresh behavior. Prefer standard validation and stable anchors; structural guards reject edits that unbalance braces. Use this tool instead of raw text for whole-method changes.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `name` | `str` | yes | Name of the script to edit |
| `path` | `str` | yes | Path to the script to edit under Assets/ directory |
| `edits` | `list[dict[str, Any]] \| str` | yes | List of edits to apply to the script (JSON list or stringified JSON) |
| `options` | `dict[str, Any] \| None` | — | Options for the script edit |
| `script_type` | `str` | — | Type of the script to edit |
| `namespace` | `str \| None` | — | Namespace of the script to edit |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
:::tip
Prefer this over [`apply_text_edits`](./apply_text_edits) for method-level changes. Structured ops keep braces balanced and survive incidental whitespace drift. Use `apply_text_edits` only when you need surgical line/column patching.
:::

### Replace a single method

> In `Assets/Scripts/PlayerController.cs`, make `HasTarget` return `currentTarget != null`.

```json
{
  "name": "PlayerController",
  "path": "Scripts/",
  "edits": [
    {
      "op": "replace_method",
      "className": "PlayerController",
      "methodName": "HasTarget",
      "replacement": "public bool HasTarget() { return currentTarget != null; }"
    }
  ],
  "options": { "validate": "standard", "refresh": "immediate" }
}
```

`path` ends with `/`. `validate: 'standard'` runs Roslyn structural checks before the write commits; use `'basic'` for cheap interior-only checks when you're touching a method body and trust the signature.

### Insert a new method at the end of a class

> Add a `Reset()` method after `Update` in `PlayerController`.

```json
{
  "name": "PlayerController",
  "path": "Scripts/",
  "edits": [
    {
      "op": "insert_method",
      "className": "PlayerController",
      "methodName": "Update",
      "position": "after",
      "replacement": "void Reset() { currentTarget = null; }"
    }
  ]
}
```

`position`: `start | end | before | after`. With `before`/`after`, `methodName` is the anchor; with `start`/`end`, it's the position within the class body.

### Delete a method

> Remove `PlayerController.LegacyTick`.

```json
{
  "name": "PlayerController",
  "path": "Scripts/",
  "edits": [
    { "op": "delete_method", "className": "PlayerController", "methodName": "LegacyTick" }
  ]
}
```

### Anchor-based insert (around a regex marker)

> Add a `Debug.Log` right after the line containing `// --- input ---`.

```json
{
  "name": "PlayerController",
  "path": "Scripts/",
  "edits": [
    {
      "op": "anchor_insert",
      "anchor": "//\\s*---\\s*input\\s*---",
      "position": "after",
      "replacement": "    Debug.Log(\"input frame\");"
    }
  ]
}
```

Anchor ops are great for adding instrumentation near stable comment markers without locking into exact line numbers. The anchor is a regex; escape literal characters.

### Apply several edits atomically

> Replace two methods AND remove a third — all in one transaction. If validation fails on any, nothing is written.

```json
{
  "name": "PlayerController",
  "path": "Scripts/",
  "edits": [
    { "op": "replace_method", "methodName": "Awake",  "replacement": "void Awake() { Init(); }" },
    { "op": "replace_method", "methodName": "Update", "replacement": "void Update() { Tick(); }" },
    { "op": "delete_method",  "methodName": "OnDestroyOld" }
  ],
  "options": { "validate": "standard" }
}
```

`className` defaults to `name` when omitted on method ops, so for single-class files you can skip it.

### After every edit

Poll `editor_state.isCompiling` until it flips back to `false`, then run [`read_console`](./read_console) to catch any compile errors before relying on the new types.
<!-- examples:end -->

