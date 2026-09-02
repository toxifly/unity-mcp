---
title: refresh_unity
sidebar_label: refresh_unity
description: "Refresh Unity's asset database and optionally request script compilation."
---

# `refresh_unity`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `core` &nbsp;·&nbsp; **Module:** `services.tools.refresh_unity`

## Description

Refresh Unity's asset database and optionally request script compilation. This mutates transient Editor state and may trigger a domain reload. mode, scope, and compile select the work; wait_for_ready can block for readiness, and job_id resumes a timed-out refresh job. A failed compile returns the first errors inline as summary.error_details, so no follow-up read_console is needed.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `mode` | `Literal['if_dirty', 'force']` | — | Refresh mode |
| `scope` | `Literal['assets', 'scripts', 'all']` | — | Refresh scope |
| `compile` | `Literal['none', 'request']` | — | Whether to request compilation |
| `wait_for_ready` | `bool` | — | If true, wait until editor_state.advice.ready_for_tools is true |
| `job_id` | `str \| None` | — | Resume a previously timed-out refresh job without requesting another refresh |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->

