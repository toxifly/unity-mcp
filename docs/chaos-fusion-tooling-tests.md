# Tooling test ownership

The following fixtures moved from the Chaos Fusion game's EditMode suite into
`TestProjects/UnityMCPTests/Assets/Tests/EditMode/`:

| Fixture | Location | Coverage |
| --- | --- | --- |
| `McpTestStagesTests` | `Services/` | Focused-before-full ordering, failure handling, input changes, and fingerprints |
| `McpObjectHandleTests` | `Services/` | Stable object/component handles, destroyed objects, and unknown/null handles |
| `ScopedPrefabStageTests` | `Tools/` | Scoped saves persist edits in an open prefab stage |

All 12 test cases and their behavioral assertions are retained. The scoped-save fixture now
creates a temporary prefab itself instead of copying a Chaos Fusion UI asset.
The tests need only this fork's Unity test project; there is no game checkout or
game assembly reference. The original Unity metadata GUIDs are retained.

`ProjectSettings/McpTestStages.json` moved into the test project too. Its input
roots now point at that project's Assets/Packages/ProjectSettings and this fork's
`MCPForUnity` package.

Run a focused selection of these fixtures first, then the fork's required Unity
suites, using its normal test workflow. Use the Editor version recorded in
`TestProjects/UnityMCPTests/ProjectSettings/ProjectVersion.txt`.

## Existing missing implementation

The published fork at the migration base (`9652515c25d5c733ac86e3ceb320214706c9c307`)
does not contain `TestStageGuard` or `TestStageState`. Those tests already required
the missing implementation in the game checkout. Migration preserves the tests
and their failure rather than skipping them or inventing replacement behavior.
Restore the corresponding fork implementation before expecting the tooling suite
to pass. This is no longer a prerequisite for ordinary Chaos Fusion tests/builds.

Unity Editor was unavailable on the migration machine, so these fixtures have
not been executed there. The game-side SDK and verification-script checks are
recorded in that repository's separation report.
