# Unity MCP efficiency and safety implementation plan

**Status:** In progress  
**Last updated:** 2026-07-10  
**Audience:** Unity MCP maintainers  
**Package used by this project:** `../../unity-mcp/MCPForUnity`  
**Primary objective:** Make common Unity automation deterministic, compact, and safe enough that an agent can modify authored assets without causing unrelated serialization churn or spending extra calls reconstructing editor state.

---

## 1. Executive summary

The current Unity MCP provides broad editor control, but several operations require expensive recovery work:

- Saving one authored scene object can serialize hundreds of unrelated prefab-instance layout overrides.
- `wait_for_ready=true` can return while compilation is still running.
- Successful test jobs can return `result: null`, forcing extra polling and interpretation.
- Console filtering can classify ordinary logs as errors/exceptions.
- The documented project workflow expects `measure_ui`, but that tool is not always registered.
- Serialized references and prefab provenance require custom `execute_code` snippets.
- Responses often contain the same payload as both encoded text and structured content.

The recommended implementation is four staged capabilities:

1. **Reliable, compact operation results and jobs.**
2. **First-class UI measurement and serialized-reference inspection.**
3. **Transactional scene/prefab mutations with an unrelated-change guard.**
4. **Optional lifecycle tracing for initialization-order bugs.**

The transaction layer is the most important safety improvement. The measurement and response changes provide the largest recurring token and latency savings.

### 1.1 Implementation progress

- [x] Canonical result envelope for built-in and globally registered custom tools.
- [x] Structured-only responses by default, without duplicated JSON text.
- [x] Standard `response_format` (`structured`, `text`, `both`) and `verbosity` (`compact`, `normal`, `detailed`) controls exposed on built-in and globally registered custom tools.
- [x] Shared terminal job summaries.
- [ ] Correct `wait_for_ready` semantics.
- [ ] Correct console log typing.

Implemented on 2026-07-10:

- Added a shared server-side result wrapper in `Server/src/services/tools/result_envelope.py`.
- Normalized legacy dictionaries, Pydantic responses, transport responses, and scalar results into the Stage 1 envelope at the MCP boundary, allowing existing Unity tool implementations to remain unchanged.
- Added `unity_instance` and measured `duration_ms` metadata, bounded messages to 160 characters, normalized warnings, and retained legacy extra fields under `data`.
- Made `structured` the default representation with an empty MCP text-content list. `text` and `both` are explicit opt-ins; detailed text is pretty-printed.
- Preserved content-bearing `ToolResult` responses such as screenshots so image/audio/file blocks are not discarded.
- Registered the canonical output schema for built-in tools and globally exposed Unity custom tools.
- Added focused envelope, formatting, signature, and compatibility tests. Validation: 16 focused tests passed across the new envelope, tool registration, refresh registration, custom-tool scope, and request-context suites; a real server construction registered all 48 built-in tools successfully. The full Server suite reached 1,331 passed and 3 skipped; its 7 failures are pre-existing/out-of-scope screenshot-parameter, `measure_ui` coverage, object-reference coercion, and sandboxed telemetry-file issues.

Implemented on 2026-07-10:

- Added a canonical terminal test-job summary (`total`, `passed`, `failed`, `skipped`, and `duration_seconds`) independently of the optional detailed result payload.
- Persisted summary and per-outcome counters in Unity `SessionState`, so domain reload can discard detailed test results without losing the terminal summary.
- Added migration behavior for jobs persisted by older package versions and a server-side compatibility normalizer for older Unity clients that return only `result.summary` or progress counters.
- Standardized task cancellation as the terminal `cancelled` lifecycle state while retaining `failed` for faults and watchdog failures.
- Added focused coverage for direct, legacy nested, and synthesized terminal summaries. Validation: all 9 async test-job integration tests passed. The full Server suite reached 1,334 passed and 3 skipped; its 7 failures remain the same pre-existing/out-of-scope screenshot-parameter, `measure_ui` coverage, object-reference coercion, and sandboxed telemetry-file issues.

---

## 2. Evidence from a real workflow

This plan comes from implementing authored UI, selection tracking, and draft-option behavior in Chaos Fusion.

### 2.1 Scene serialization churn

Adding one full-screen outside-click catcher and saving `ChaosFusionDemo.unity` produced roughly 1,300 lines of unrelated RectTransform changes. The intended scene change was only one manager/prefab replacement.

Recovery required:

1. Capturing the intended serialized objects.
2. Reconstructing the scene from the clean Git blob in chunks.
3. Reapplying only the intended prefab instance.
4. Reloading without saving again.

This was the largest source of time, tool calls, and risk.

### 2.2 Missing deterministic UI measurement

The repository workflow specifies `measure_ui`, but it was not present in the registered tool list. Custom C# was required to compare:

- RectTransform world corners;
- canvas pixel bounds;
- authored overlay anchors and offsets.

The first custom measurement compared incompatible coordinate systems (`Screen.width` versus the editor Game View canvas), requiring a second call.

### 2.3 Incomplete readiness semantics

`refresh_unity(wait_for_ready=true)` returned a successful response whose resulting state was still `compiling`. The caller then had to make another tool call and infer readiness from console behavior.

### 2.4 Inconsistent test completion payloads

`get_test_job` sometimes returned:

```json
{
  "status": "succeeded",
  "progress": { "completed": 20, "total": 20, "failures_so_far": [] },
  "result": null
}
```

The status was usable, but the missing summary prevented a uniform completion path.

### 2.5 Console misclassification

An error-only console query returned ordinary `CfLog.Log` output and the test runner's “Saving results” message as exceptions. This makes a clean compile/test gate ambiguous.

### 2.6 Response duplication

Many responses repeated the same result in:

- `content[0].text` as JSON text; and
- `structuredContent` as an object.

Callers only need one canonical machine-readable representation.

### 2.7 Lifecycle-dependent UI state

The draft highlight regression depended on this sequence:

```text
authored inactive option
  -> configured as InspectOnly
  -> first OnEnable
  -> selection
  -> backend refresh
  -> DraftUnit reconciled to RosterUnit
  -> Claim completion
```

Lifecycle/event tracing would have exposed the state transition earlier than repeated static asset inspection.

---

## 3. Goals

### 3.1 Functional goals

- Modify a scene or prefab while proving that only expected objects/properties changed.
- Roll back an operation if unexpected serialized changes appear.
- Measure UI in a declared coordinate system without custom C#.
- Inspect serialized references, missing references, and prefab provenance directly.
- Treat compilation and test execution as reliable jobs with terminal summaries.
- Return compact structured responses by default.
- Preserve inactive authored objects in queries and measurements.

### 3.2 Safety goals

- Never overwrite a user's already-dirty scene silently.
- Never save unrelated editor/layout changes as a side effect of a scoped operation.
- Make dry-run and rollback behavior explicit.
- Report exactly which assets, objects, and properties changed.
- Keep read-only tools genuinely read-only.

### 3.3 Efficiency goals

- Reduce the normal UI verification loop to one measurement call.
- Reduce compile verification to one blocking job call.
- Reduce successful test verification to one start call plus at most one wait call.
- Avoid duplicated response bodies.
- Avoid large component payloads when only a few serialized properties are requested.

---

## 4. Non-goals

- Replacing Unity's serializer.
- Editing Unity YAML directly as the normal mutation path.
- General visual/aesthetic review; geometry is deterministic, aesthetics remain human judgment.
- Automatically resolving merge conflicts.
- Automatically discarding a user's pre-existing dirty scene state.
- Building a general profiler or frame debugger in this phase.

---

## 5. Design principles

1. **Structured data is canonical.** Human-readable text is optional.
2. **Coordinate systems must be explicit.** Never mix screen, canvas, local, and world bounds silently.
3. **Mutations are transactions.** Identify scope, mutate, validate, commit or roll back.
4. **Dirty state belongs to the user.** Refuse ambiguous saves by default.
5. **Terminal jobs always have terminal summaries.** A succeeded job cannot have an absent result.
6. **Queries are whitelist-first.** Return only requested properties unless exploration is explicitly requested.
7. **Prefab provenance is first-class.** Scene objects, prefab instances, overrides, and source objects must be distinguishable.

---

## 6. Proposed implementation stages

### Stage 1 — Reliable results, jobs, and token-efficient envelopes

#### 6.1 Canonical result envelope

**Implementation status:** Complete (2026-07-10). See [Implementation progress](#11-implementation-progress) for files and validation.

Use one structured response shape across tools:

```json
{
  "success": true,
  "status": "completed",
  "message": "Optional short human summary",
  "data": {},
  "warnings": [],
  "error": null,
  "meta": {
    "unity_instance": "ChaosFusion@abc123",
    "duration_ms": 412
  }
}
```

Rules:

- Return this object in `structuredContent`.
- Do not also JSON-encode it into `content[].text` by default.
- Add `response_format: "structured" | "text" | "both"`; default to `structured`.
- Keep `message` under roughly 160 characters.
- Put verbose diagnostics behind `verbosity: "compact" | "normal" | "detailed"`.

#### 6.2 Shared job contract

**Implementation status:** Complete (2026-07-10) for the shared contract and test jobs. Compilation will consume the same summary shape as part of the next `wait_for_ready` step.

Compilation and tests should use the same lifecycle:

```text
queued -> running -> succeeded | failed | cancelled | timed_out
```

Every terminal job must include:

```json
{
  "summary": {
    "total": 20,
    "passed": 20,
    "failed": 0,
    "skipped": 0,
    "duration_seconds": 11.7
  }
}
```

For compilation, use analogous fields:

```json
{
  "summary": {
    "compiled": true,
    "errors": 0,
    "warnings": 2,
    "duration_seconds": 4.1
  }
}
```

#### 6.3 Correct `wait_for_ready`

`refresh_unity(wait_for_ready=true)` must not return success while the editor is compiling, updating, entering Play Mode, or in domain reload.

Implementation behavior:

1. Request refresh/compile.
2. Observe the editor state across the domain reload boundary.
3. Require a stable ready state for at least two consecutive editor updates.
4. Return a terminal compile summary.
5. On timeout, return `status: "timed_out"` with the last observed state and a reusable job ID.

Do not treat “compile requested” as “compile completed.”

#### 6.4 Fix console typing

Return Unity's actual log type separately from any MCP transport classification:

```json
{
  "unity_log_type": "Log",
  "source": "user",
  "message": "Selected roster unit...",
  "stack_trace": null
}
```

Recommended `source` values:

- `user`
- `compiler`
- `test_runner`
- `mcp`
- `unity_internal`

Filtering by `types=["error"]` must include only `Error`, `Assert`, and `Exception` unless the caller opts into a broader mapping.

#### 6.5 Stage 1 acceptance tests

- A successful test job always has a non-null summary.
- `wait_for_ready=true` never returns `resulting_state: "compiling"`.
- A normal `Debug.Log` is excluded from an error-only query.
- The default response does not duplicate structured JSON as text.
- Domain reload does not lose the compile/test job record.

---

### Stage 2 — UI measurement and serialized inspection

#### 6.6 Implement and always register `measure_ui`

Proposed request:

```json
{
  "targets": ["Btn_Draft", "Btn_Breed"],
  "container": null,
  "include_children": false,
  "include_inactive": true,
  "space": "canvas",
  "reference": "RootCanvas",
  "assertions": [
    { "type": "no_overlap", "targets": ["Btn_Draft", "Btn_Breed"] },
    { "type": "inside", "target": "Btn_Draft", "container": "RootCanvas" }
  ]
}
```

Supported spaces:

- `world`
- `screen_pixels`
- `canvas`
- `local`

Each result must state its space and reference:

```json
{
  "target": "SelectionOutsideClickCatcher",
  "active_self": true,
  "active_in_hierarchy": true,
  "space": "canvas",
  "reference": "RootCanvas",
  "bounds": { "x_min": -1920, "y_min": -1080, "x_max": 1920, "y_max": 1080 },
  "size": { "width": 3840, "height": 2160 },
  "clipped": false
}
```

Useful assertions:

- `inside`
- `covers`
- `matches_bounds`
- `no_overlap`
- `minimum_gap`
- `on_screen`
- `not_clipped`
- `ordered_left_to_right`
- `ordered_top_to_bottom`

The tool should calculate all target bounds in one editor invocation.

#### 6.7 Implement `inspect_serialized`

Proposed request:

```json
{
  "targets": ["SelectionManager", "UnitSlotController"],
  "properties": ["outsideClickCatcher", "breedingStateOverlay"],
  "include_prefab_provenance": true,
  "include_missing_references": true
}
```

Proposed result for an object reference:

```json
{
  "target": "SelectionManager",
  "component": "ChaosFusion.SelectionManager",
  "property": "outsideClickCatcher",
  "value_kind": "object_reference",
  "referenced_object": "SelectionOutsideClickCatcher",
  "referenced_type": "UnityEngine.GameObject",
  "missing": false,
  "prefab": {
    "is_instance": true,
    "source_asset": "Assets/Prefabs/UI/SelectionManager.prefab",
    "is_override": false
  }
}
```

Implementation notes:

- Use `SerializedObject`/`SerializedProperty`, not reflection-only field reads.
- Resolve targets by GlobalObjectId when possible.
- Support component type plus hierarchy path to disambiguate duplicate names.
- Respect explicit property whitelists.
- Report broken/missing object references distinctly from `null`.

#### 6.8 Add compact prefab/scene provenance queries

Expose:

- asset path;
- scene path;
- GlobalObjectId;
- instance root;
- source prefab object;
- nearest and outermost prefab instance roots;
- override property paths;
- added/removed component state.

This should be available without dumping the whole component.

#### 6.9 Stage 2 acceptance tests

- A full-screen ScreenSpaceOverlay child reports bounds equal to its canvas pixel rect.
- An inactive RectTransform is measurable.
- Two measurements in different spaces cannot be compared without an explicit conversion/reference.
- A serialized prefab reference resolves to its source asset and reports whether it is overridden.
- Missing references are returned as structured findings, not omitted properties.

---

### Stage 3 — Transactional scene and prefab mutations

#### 6.10 Add a mutation transaction layer

All scoped asset mutations should run through a shared transaction service:

```text
preflight -> snapshot -> mutate -> validate -> preview changes -> commit | rollback
```

##### Preflight

- Resolve every target to a GlobalObjectId.
- Record loaded scenes and active scene.
- Detect dirty scenes/assets.
- Refuse by default if a target scene was already dirty.
- Allow explicit `dirty_scene_policy: "reject" | "preserve" | "allow"`.

##### Snapshot

Capture:

- target asset bytes or a safe temporary copy;
- scene dirty state;
- Undo group;
- serialized fingerprints for all objects in the asset;
- prefab instance override lists;
- active prefab stage and scene setup.

The byte snapshot is a rollback safety net, not the normal editing mechanism.

##### Mutate

- Use Unity APIs (`SerializedObject`, `PrefabUtility`, `EditorSceneManager`) for changes.
- Keep all changes in one Undo group.
- Do not call `SaveAssets` globally when one asset is in scope.

##### Validate

- Recompute object/property fingerprints before saving.
- Compare the actual changed set with the declared expected scope.
- Detect added, removed, and modified objects/properties.
- Run caller-provided validators before commit.

##### Commit or rollback

- If the changed set is allowed, save only the target assets.
- If unexpected changes exist, roll back the Undo group before saving.
- If save-time serialization introduces unexpected file changes, restore the byte snapshot, reimport, and report the rejected change set.
- Restore the original scene/prefab-stage setup in either case.

#### 6.11 Add `change_guard` options to mutation tools

Example:

```json
{
  "change_guard": {
    "mode": "reject_unexpected",
    "expected_objects": ["SelectionManager", "SelectionOutsideClickCatcher"],
    "expected_properties": [
      "SelectionManager.outsideClickCatcher",
      "SelectionManager.Transform.m_Children"
    ],
    "max_changed_objects": 2
  }
}
```

Response:

```json
{
  "committed": false,
  "rolled_back": true,
  "unexpected_changes": [
    {
      "object": "HudPanel/Btn_Breed",
      "property": "RectTransform.m_AnchoredPosition.x",
      "before": 0,
      "after": 440
    }
  ]
}
```

#### 6.12 Implement dry-run change previews

Every mutation tool should accept `dry_run: true`.

Dry-run must:

- perform the change inside an Undo/temporary transaction;
- return the exact object/property change set;
- roll back without saving;
- leave dirty state unchanged.

#### 6.13 Atomic prefab creation and scene replacement

Add a high-level operation:

```json
{
  "action": "create_and_replace",
  "target": "SelectionManager",
  "prefab_path": "Assets/Prefabs/UI/SelectionManager.prefab",
  "preserve_world_transform": true,
  "preserve_scene_references": true,
  "save_scene": true,
  "change_guard": { "mode": "reject_unexpected" }
}
```

Required behavior:

1. Create the prefab asset from the complete hierarchy.
2. Replace the scene object with a connected instance.
3. Preserve external serialized references where Unity supports remapping.
4. Preserve hierarchy position and transform.
5. Validate that only the target hierarchy and scene root/reference records changed.
6. Save prefab and scene as one logical transaction.
7. Roll both back if either save fails or introduces unrelated changes.

Optional mode:

```json
{ "link_scene_instance": false }
```

This creates the prefab without dirtying or replacing the scene object.

#### 6.14 Save-specific APIs

Avoid one broad `manage_scene(action="save")` path for every case. Provide:

- `save_scene_scoped`
- `save_prefab_scoped`
- `save_assets_scoped`
- `preview_asset_changes`

Each should report:

- assets saved;
- objects changed;
- properties changed;
- dirty assets left unsaved;
- whether rollback occurred.

#### 6.15 Stage 3 acceptance tests

- Adding one child does not commit unrelated RectTransform overrides.
- If a layout callback dirties an unrelated object, the transaction rejects and restores the asset.
- A pre-dirty scene is rejected under the default policy.
- Dry-run leaves the scene byte-identical and preserves its prior dirty flag.
- Prefab creation plus replacement preserves external references and hierarchy order.
- A failure saving the scene also rolls back the newly created prefab asset.
- Loaded scenes and prefab stage are restored after success and failure.

---

### Stage 4 — Lifecycle and event tracing

#### 6.16 Add opt-in lifecycle tracing

Proposed request:

```json
{
  "action": "start",
  "targets": ["DraftPanel/DraftOption (0)", "SelectionManager"],
  "events": [
    "Awake",
    "OnEnable",
    "OnDisable",
    "OnDestroy",
    "serialized_property_change",
    "selection_change"
  ],
  "property_whitelist": [
    "UnitSlotController.interactionMode",
    "Image.color",
    "SelectionManager.CurrentSelection"
  ],
  "max_events": 500
}
```

The trace should return a compact ordered timeline:

```json
{
  "sequence": 17,
  "frame": 122,
  "object": "DraftOption (0)",
  "event": "OnEnable",
  "changes": [
    { "property": "Image.color", "before": "yellow", "after": "clear" }
  ]
}
```

#### 6.17 Implementation constraints

- Opt-in only; no permanent instrumentation in user scripts.
- Prefer editor hooks, temporary proxy components, and existing Unity callbacks.
- Cap events and payload size.
- Preserve ordering with a monotonic sequence number.
- Clearly distinguish observed changes from inferred causality.
- Automatically detach instrumentation on stop, domain reload, or timeout.

#### 6.18 Stage 4 acceptance tests

- Trace inactive-to-active prefab instances across `Awake` and `OnEnable`.
- Maintain ordering across one domain reload where supported.
- Stop removes all temporary hooks/components.
- Event caps truncate with an explicit `truncated: true` marker.

---

## 7. Cross-cutting implementation details

### 7.1 Stable object identity

Names are convenient but ambiguous. Internally use:

1. GlobalObjectId for persistent scene/prefab objects.
2. Asset GUID plus local file ID for asset objects.
3. Instance ID only for temporary runtime objects.
4. Hierarchy path as a human-readable label, not the primary key.

### 7.2 Serialized fingerprints

For change guards, fingerprint normalized serialized properties rather than relying only on file diffs.

Recommended record:

```text
asset GUID
GlobalObjectId/local file ID
component type
property path
normalized value
object-reference GlobalObjectId
```

Ignore known volatile/editor-only properties through an explicit allowlist, not a silent catch-all.

### 7.3 Payload limits

- Default to summaries.
- Page object/property lists.
- Return full before/after values only for changed properties.
- Allow `include_unchanged=false` by default.
- Use property whitelists for component inspection.
- Never embed screenshots or previews unless requested.

### 7.4 Capability discovery

Expose a compact capability resource:

```json
{
  "tools": {
    "measure_ui": { "version": 1 },
    "inspect_serialized": { "version": 1 },
    "mutation_transactions": { "version": 1 }
  }
}
```

Project instructions can then fail early when a required custom tool is unavailable.

### 7.5 Error taxonomy

Use stable codes, for example:

- `EDITOR_NOT_READY`
- `TARGET_NOT_FOUND`
- `TARGET_AMBIGUOUS`
- `SCENE_ALREADY_DIRTY`
- `UNEXPECTED_SERIALIZED_CHANGES`
- `ROLLBACK_FAILED`
- `COMPILE_FAILED`
- `TEST_JOB_LOST`
- `COORDINATE_REFERENCE_REQUIRED`

Messages should explain remediation, but callers should branch on codes.

---

## 8. Recommended delivery order

### Milestone A — Results and readiness

- Canonical response envelope.
- Remove duplicated JSON text.
- Shared terminal job summaries.
- Correct `wait_for_ready` semantics.
- Correct console log typing.

**Why first:** Smallest architectural risk and immediate savings across every workflow.

### Milestone B — Read-only verification tools

- `measure_ui`.
- `inspect_serialized`.
- prefab provenance query.
- capability discovery.

**Why second:** Read-only tools are low risk and eliminate repeated custom C#.

### Milestone C — Transactional mutations

- Shared transaction service.
- dry-run previews.
- change guards.
- scoped saves.
- atomic prefab create-and-replace.

**Why third:** Highest value, but it needs broad integration tests around Undo, dirty scenes, imports, and rollback.

### Milestone D — Lifecycle tracing

- bounded trace sessions.
- property snapshots.
- event timeline.

**Why last:** Valuable for difficult bugs, but less common than measurement, compilation, and asset mutation.

---

## 9. Test strategy

### 9.1 Editor integration fixture

Create a small dedicated Unity project/fixture containing:

- one ScreenSpaceOverlay canvas at a non-default resolution;
- active and inactive UI objects;
- nested prefab instances with overrides;
- a scene that deliberately becomes dirty through a layout callback;
- a component with valid, null, and missing serialized references;
- EditMode and PlayMode tests with pass, fail, skip, and expected logs;
- a script whose edit triggers domain reload.

### 9.2 Golden tests

Keep golden structured responses for:

- compact operation envelopes;
- measurement coordinate spaces;
- serialized-reference inspection;
- expected and unexpected mutation change sets;
- terminal compile/test summaries.

Avoid golden tests over raw Unity YAML, which is version-sensitive.

### 9.3 Failure injection

Test:

- compile failure;
- test runner exception;
- domain reload during job polling;
- prefab save success followed by scene save failure;
- rollback failure;
- target destroyed during operation;
- pre-existing dirty scene;
- ambiguous duplicate object names;
- output truncation and paging.

### 9.4 Compatibility matrix

At minimum validate against every Unity version officially supported by MCPForUnity. Transaction and serialization behavior is particularly version-sensitive.

---

## 10. Definition of done

The plan is complete when all of the following hold:

- A caller can add an authored prefab child to a scene and prove no unrelated object changed.
- Unexpected layout serialization causes automatic rollback, not a dirty commit.
- A full-screen catcher and a matching unit overlay can be verified with one `measure_ui` call.
- Authored references and prefab origins can be verified without `execute_code`.
- `refresh_unity(wait_for_ready=true)` returns only after compilation reaches a terminal state.
- Every completed test job has a summary.
- Error-only console queries contain only real errors/asserts/exceptions.
- Default tool responses contain one structured payload, not duplicated JSON.
- All new APIs have bounded responses, paging where needed, stable error codes, and integration tests.

---

## 11. Expected impact

For workflows like the one that motivated this plan:

- UI geometry verification drops from multiple custom-code calls to one measurement call.
- Authored-reference verification drops from custom C# to one compact inspection call.
- Compilation verification drops from refresh plus polling plus console inference to one blocking job result.
- Test completion becomes uniform and requires no interpretation of `result: null`.
- The scene-reserialization recovery sequence disappears; an unsafe save is rejected and rolled back before it can pollute the worktree.
- Typical MCP response size falls materially by removing duplicated envelopes and returning summaries by default.

The larger benefit is confidence: agents can make narrow Unity asset changes while producing evidence that the saved result is equally narrow.
