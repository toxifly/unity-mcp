---
title: get_test_job
sidebar_label: get_test_job
description: "Read the status or results of an asynchronous Unity test job by job_id."
---

# `get_test_job`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `testing` &nbsp;·&nbsp; **Module:** `services.tools.run_tests`

## Description

Read the status or results of an asynchronous Unity test job by job_id. Polling is read-only; include_failed, include_skipped and include_details control returned result detail.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `job_id` | `str` | yes | Job id returned by run_tests |
| `include_failed` | `bool` | — | Include details for failed tests (default: false) |
| `include_skipped` | `bool` | — | Include details for skipped tests. Off by default: result.skipped_reasons already carries them as reason -> count. |
| `include_details` | `bool` | — | Include details for all tests (default: false) |
| `wait_timeout` | `int \| None` | — | If set, wait up to this many seconds for tests to complete before returning. Reduces polling frequency and avoids client-side loop detection. Recommended: 30-60 seconds. Returns immediately if tests complete sooner. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->

