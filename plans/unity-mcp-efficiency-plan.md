  1. Strict test-filter validation

  The installed run_tests expects camelCase fields such as testNames, while the project guidance uses snake_case examples. Unknown fields were silently ignored, causing an unintended
  full PlayMode run.

  Proposed changes:

  - Accept both testNames and test_names.
  - Reject unknown parameters instead of silently ignoring them.
  - Echo the normalized filter in the start response.
  - Warn or fail when filter-like parameters were supplied but no filter was recognized.

  2. Fail fast when compiling during Play Mode

  refresh_unity(wait_for_ready=true) waited 60 seconds while compilation was blocked by Play Mode.

  Proposed changes:

  - Return blocked_by_play_mode immediately.
  - Include actionable advice: “Stop Play Mode, then retry.”
  - Optionally support an explicit stop_play_mode=true; never stop automatically by default.

  3. Make asset moves transactional and truthful

  manage_asset(move) reported failure after successfully moving both the script and .meta file. That creates a dangerous retry situation.

  Proposed changes:

  - Verify source and destination after AssetDatabase.MoveAsset.
  - Return one of: moved, not_moved, or ambiguous.
  - Include the preserved GUID in the response.
  - Treat “destination exists and source is absent with the same GUID” as successful completion.

  4. Add scoped mutation guards

  Saving the prefab stage caused unrelated ExecuteAlways, TMP, and layout serialization changes. The test runner also produced extensive unrelated scene changes during its failed broad
  run.

  Proposed changes:

  - Add dry_run, change_guard, and expected-path/property allowlists to prefab and scene mutations.
  - Snapshot the asset before editing and report the serialized diff before saving.
  - Refuse scoped saves when unrelated objects or properties changed unless explicitly allowed.
  - Restore the previously loaded scene after tests without saving incidental layout state.

  This is the most valuable safety improvement after strict test filtering.

  5. Validate serialized object references after mutation

  Setting arrays with bare instance IDs reported success but produced four null references. Wrapping entries as {instanceID: ...} worked.

  Proposed changes:

  - Make the schema explicit for arrays of object references.
  - Reject unsupported bare-ID arrays rather than coercing them to null.
  - Read the property back after mutation and fail if requested non-null references became null.
  - Return the resolved object names and IDs.

  6. Make test-job status independent of the Unity main thread

  During the stalled test run, both status polling and stop commands timed out. The watchdog only surfaced the stalled state later.

  Proposed changes:

  - Keep job status and watchdog bookkeeping accessible off the Unity main thread.
  - Run stall detection continuously, not only when get_test_job is called.
  - Allow clear_stuck and cancellation even when the editor main thread is unavailable.
  - Report phases such as building, entering_play_mode, running, and exiting_play_mode.

  7. Restore the documented capabilities and UI-measurement surface

  The project expects mcpforunity://capabilities and measure_ui, but the capabilities resource was absent and the instance reported project_scoped_tools: false.

  Proposed changes:

  - Always expose a capabilities resource, even when no project tools are registered.
  - Explain why project-scoped tools failed to load.
  - Ship measure_ui as a standard UI-group tool or provide a stable equivalent.
  - Support filtered prefab/UI inspection using property_whitelist.

  8. Improve dynamic tool-group discovery

  Activating testing or ui reported success, but newly enabled tools did not consistently appear in the callable tool listing; sync then reported only core.

  Proposed changes:

  - Make activation state stable for the client session.
  - Return complete callable schemas on activation.
  - Add a generic group-tool invocation endpoint so dynamic discovery is not client-dependent.
  - Ensure sync reflects the same session modified by activate.