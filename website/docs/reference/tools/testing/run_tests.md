---
title: run_tests
sidebar_label: run_tests
description: "Start an asynchronous Unity Test Framework run and return a job_id immediately."
---

# `run_tests`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `testing` &nbsp;·&nbsp; **Module:** `services.tools.run_tests`

## Description

Start an asynchronous Unity Test Framework run and return a job_id immediately. This mutates transient test and Editor state; mode, test_names, group_names, category_names, and assembly_names filter the run.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `mode` | `Literal['EditMode', 'PlayMode']` | — | Unity test mode to run |
| `test_names` | `list[str] \| str \| None` | — | Full names of specific tests to run |
| `group_names` | `list[str] \| str \| None` | — | Same as test_names, except it allows for Regex |
| `category_names` | `list[str] \| str \| None` | — | NUnit category names to filter by |
| `assembly_names` | `list[str] \| str \| None` | — | Assembly names to filter tests by |
| `include_failed` | `bool` | — | Include details for failed tests (default: false) |
| `include_skipped` | `bool` | — | Include details for skipped tests. Off by default: a suite with a standing [Explicit] block re-sends the same sentences on every green run, and result.skipped_reasons already carries them as counts. |
| `include_details` | `bool` | — | Include details for all tests (default: false) |
| `wait_timeout` | `int \| None` | — | If set, wait up to this many seconds for the run to finish and return its result, instead of returning a job_id to poll. Saves the whole start-then-poll round trip on runs short enough to sit through. |
| `init_timeout` | `int \| None` | — | Initialization timeout in milliseconds. PlayMode tests may need longer due to domain reload (default: 15000). Recommended: 120000 for PlayMode. |
| `clear_stuck` | `bool` | — | Recovery escape hatch: force-clear a wedged test job instead of starting a run. Use when get_test_job stays 'running' forever or run_tests keeps returning 'tests_running' after a runner crash. Bypasses preflight. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->

