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
| `include_failed_tests` | `bool` | — | Include details for failed/skipped tests only (default: false) |
| `include_details` | `bool` | — | Include details for all tests (default: false) |
| `init_timeout` | `int \| None` | — | Initialization timeout in milliseconds. PlayMode tests may need longer due to domain reload (default: 15000). Recommended: 120000 for PlayMode. |
| `clear_stuck` | `bool` | — | Recovery escape hatch: force-clear a wedged test job instead of starting a run. Use when get_test_job stays 'running' forever or run_tests keeps returning 'tests_running' after a runner crash. Bypasses preflight. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->

