---
title: import_model_file
sidebar_label: import_model_file
description: "Copy a local FBX, OBJ, glTF/GLB, or ZIP model into Assets/ and run Unity's model importer. source_path identifies the local file; name, output_folder, target_size, and animation_type configure the import."
---

# `import_model_file`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `asset_gen` &nbsp;·&nbsp; **Module:** `services.tools.import_model_file`

## Description

Copy a local FBX, OBJ, glTF/GLB, or ZIP model into Assets/ and run Unity's model importer. source_path identifies the local file; name, output_folder, target_size, and animation_type configure the import. Use generic or humanoid animation_type for rigged FBX/OBJ clips; none imports no rig, and glTF/GLB ignore this setting because glTFast handles animation. glTF/GLB requires glTFast. ZIP multi-file exports so external .bin, .mtl, and texture sidecars are copied. The operation writes project assets but sends no source-file bytes or API keys over the bridge.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `source_path` | `str` | yes | Path to the model file on disk (.fbx/.obj/.glb/.gltf/.zip). |
| `name` | `str \| None` | — | Base name for the imported asset. |
| `output_folder` | `str \| None` | — | Destination folder under Assets/ for the import. |
| `target_size` | `float \| None` | — | Normalize the largest dimension to this size (meters). |
| `animation_type` | `Literal['none', 'generic', 'humanoid', 'legacy'] \| None` | — | FBX/OBJ only: rig/animation import mode. 'generic' or 'humanoid' surface the model's AnimationClips; 'legacy' selects Unity's legacy Animation system (rarely needed); omitted or 'none' imports no rig. Ignored for glTF/GLB. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->

