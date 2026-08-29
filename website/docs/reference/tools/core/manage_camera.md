---
title: manage_camera
sidebar_label: manage_camera
description: "Manage Unity and Cinemachine cameras: setup, creation, configuration, blending, and capture. action supports ping, ensure_brain, get_brain_status, create_camera, set_target, set_priority, set_lens, set_body, set_aim, set_noise, add_exten…"
---

# `manage_camera`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `core` &nbsp;·&nbsp; **Module:** `services.tools.manage_camera`

## Description

Manage Unity and Cinemachine cameras: setup, creation, configuration, blending, and capture. action supports ping, ensure_brain, get_brain_status, create_camera, set_target, set_priority, set_lens, set_body, set_aim, set_noise, add_extension, remove_extension, set_blend, force_camera, release_override, list_cameras, screenshot, and screenshot_multiview. properties contains action-specific settings; capture actions use the dedicated screenshot, view, orbit, and output parameters. Cinemachine-only features require its package, while create_camera falls back to a basic Camera. Omitting camera from a screenshot captures Screen Space - Overlay UI; direct camera rendering excludes those canvases. Configuration and control actions mutate scene or Editor state, and capture actions can write image files.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `action` | `str` | yes | The camera action to perform. |
| `target` | `str \| None` | — | Target camera (name, path, or instance ID). |
| `search_method` | `Literal['by_id', 'by_name', 'by_path'] \| None` | — | How to find target. |
| `properties` | `dict[str, Any] \| str \| None` | — | Action-specific parameters (dict or JSON string). |
| `screenshot_file_name` | `str \| None` | — | Screenshot file name (optional). Defaults to timestamp. |
| `screenshot_super_size` | `int \| str \| None` | — | Screenshot supersize multiplier (integer >= 1). |
| `camera` | `str \| None` | — | Camera to capture from (name, path, or instance ID). Omit to use ScreenCapture API (captures all layers including Screen Space Overlay UI). Specify only when you need a particular camera viewpoint; note that Screen Space - Overlay canvases will NOT appear in camera-rendered captures. |
| `include_image` | `bool \| str \| None` | — | If true, return screenshot as inline base64 PNG. Default false. |
| `max_resolution` | `int \| str \| None` | — | Max resolution (longest edge px) for inline image. Default 640. |
| `capture_source` | `Literal['game_view', 'scene_view'] \| None` | — | Screenshot source. 'game_view' (default) captures the game/camera path; 'scene_view' captures the active Unity Scene View viewport. |
| `batch` | `str \| None` | — | Batch capture mode: 'surround' (6 angles) or 'orbit' (configurable grid). |
| `view_target` | `str \| int \| list[float] \| None` | — | Target to focus on. GameObject name/path/ID or [x,y,z]. For game_view: aims camera at target. For scene_view: frames the Scene View on the target. |
| `view_position` | `list[float] \| str \| None` | — | World position [x,y,z] to place camera for positioned capture. |
| `view_rotation` | `list[float] \| str \| None` | — | Euler rotation [x,y,z] for camera. Overrides view_target if both provided. |
| `orbit_angles` | `int \| str \| None` | — | Number of azimuth samples for batch='orbit' (default 8, max 36). |
| `orbit_elevations` | `list[float] \| str \| None` | — | Elevation angles in degrees for batch='orbit' (default [0, 30, -15]). |
| `orbit_distance` | `float \| str \| None` | — | Camera distance from target for batch='orbit' (default auto). |
| `orbit_fov` | `float \| str \| None` | — | Camera FOV in degrees for batch='orbit' (default 60). |
| `output_folder` | `str \| None` | — | Optional folder for screenshot output. Project-relative (e.g. 'Assets/Screenshots' or 'Captures') or absolute path inside the project. Overrides the user's Editor preference. If omitted, falls back to the Editor preference, then to the built-in default (Assets/Screenshots). |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->

