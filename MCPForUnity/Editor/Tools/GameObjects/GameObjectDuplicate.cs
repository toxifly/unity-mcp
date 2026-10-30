#nullable disable
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using MCPForUnity.Runtime.Helpers;

namespace MCPForUnity.Editor.Tools.GameObjects
{
    internal static class GameObjectDuplicate
    {
        internal static object Handle(JObject @params, JToken targetToken, string searchMethod)
        {
            GameObject sourceGo = ManageGameObjectCommon.FindObjectInternal(targetToken, searchMethod);
            if (sourceGo == null)
            {
                return new ErrorResponse($"Target GameObject ('{targetToken}') not found using method '{searchMethod ?? "default"}'.");
            }

            string newName = @params["new_name"]?.ToString();
            Vector3? position = VectorParsing.ParseVector3(@params["position"]);
            Vector3? offset = VectorParsing.ParseVector3(@params["offset"]);
            JToken parentToken = @params["parent"];

            bool reparent = parentToken != null;
            Transform requestedParent = null;
            if (reparent && parentToken.Type != JTokenType.Null
                && !(parentToken.Type == JTokenType.String && string.IsNullOrEmpty(parentToken.ToString())))
            {
                GameObject parentGo = ManageGameObjectCommon.FindObjectInternal(parentToken, "by_id_or_name_or_path");
                if (parentGo == null)
                {
                    return new ErrorResponse($"Parent ('{parentToken}') not found.");
                }
                requestedParent = parentGo.transform;
            }

            int undoGroup = Undo.GetCurrentGroup();
            GameObject duplicatedGo = DuplicateLikeEditor(sourceGo);
            if (duplicatedGo == null)
            {
                return new ErrorResponse($"Unity did not duplicate '{sourceGo.name}'.");
            }

            // Undo.RecordObject also records the rename as a prefab-instance override.
            Undo.RecordObject(duplicatedGo, $"Duplicate {sourceGo.name}");
            duplicatedGo.name = !string.IsNullOrEmpty(newName)
                ? newName
                : sourceGo.name.Replace("(Clone)", "").Trim() + "_Copy";

            // Local values are kept: a world-space round trip through a zero-scale or zero-size
            // parent chain cannot be inverted and corrupts the copy's local scale and position.
            if (reparent && duplicatedGo.transform.parent != requestedParent)
            {
                duplicatedGo.transform.SetParent(requestedParent, false);
            }

            // Only the prefab contents root's hierarchy is saved; like Edit > Duplicate in a prefab
            // stage, a copy left at the stage's scene root (or in its UI environment canvas) goes
            // under the prefab contents root.
            PrefabStage prefabStage = PrefabStageUtility.GetPrefabStage(duplicatedGo);
            Transform contentsRoot = prefabStage != null ? prefabStage.prefabContentsRoot.transform : null;
            if (contentsRoot != null && !duplicatedGo.transform.IsChildOf(contentsRoot))
            {
                duplicatedGo.transform.SetParent(contentsRoot, false);
            }

            if (position.HasValue || offset.HasValue)
            {
                Undo.RecordObject(duplicatedGo.transform, $"Duplicate {sourceGo.name}");
                duplicatedGo.transform.position = position ?? (sourceGo.transform.position + offset.Value);
            }

            EditorUtility.SetDirty(duplicatedGo);
            // The duplicate's own blocks are new (auto-scoped); the parent it landed under gains
            // an m_Children entry on its Transform.
            Services.MutationTransactions.SceneMutationLedger.Record(duplicatedGo.transform.parent);
            EditorSceneManager.MarkSceneDirty(duplicatedGo.scene);
            Undo.CollapseUndoOperations(undoGroup);

            return new SuccessResponse(
                $"Duplicated '{sourceGo.name}' as '{duplicatedGo.name}'.",
                new
                {
                    originalName = sourceGo.name,
                    originalId = sourceGo.GetInstanceIDCompat(),
                    duplicatedObject = Helpers.GameObjectSerializer.GetGameObjectData(duplicatedGo)
                }
            );
        }

        // Runs Edit > Duplicate's engine path on the source alone. The copy lands in the source's
        // scene (prefab stage included), right after the source under the same parent, with its
        // serialized local values. A prefab instance stays linked to its asset with all overrides
        // (property modifications, added/removed components and GameObjects) and references
        // inside the copied hierarchy remapped; Object.Instantiate would unpack it into a plain
        // clone in the active scene. The copy is left selected, as Edit > Duplicate leaves it.
        private static GameObject DuplicateLikeEditor(GameObject source)
        {
            Selection.objects = new Object[] { source };
            Unsupported.DuplicateGameObjectsUsingPasteboard();
            GameObject[] copies = Selection.gameObjects;
            return copies.Length == 1 && copies[0] != source ? copies[0] : null;
        }
    }
}
