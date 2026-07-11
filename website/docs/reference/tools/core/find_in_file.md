---
title: find_in_file
sidebar_label: find_in_file
description: "Search a file identified by uri with a regular-expression pattern."
---

# `find_in_file`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `core` &nbsp;·&nbsp; **Module:** `services.tools.find_in_file`

## Description

Search a file identified by uri with a regular-expression pattern. This tool is read-only and returns matching line numbers and excerpts; invalid regular expressions fail without reading matches.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `uri` | `str` | yes | The resource URI to search under Assets/ or file path form supported by read_resource |
| `pattern` | `str` | yes | The regex pattern to search for |
| `project_root` | `str \| None` | — | Optional project root path |
| `max_results` | `int` | — | Cap results to avoid huge payloads |
| `ignore_case` | `bool \| str \| None` | — | Case insensitive search |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->

