#nullable disable
using System.Collections.Generic;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services.MutationTransactions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using MCPForUnity.Runtime.Helpers;

namespace MCPForUnity.Editor.Tools.GameObjects
{
    internal static class GameObjectDelete
    {
        internal static object Handle(JToken targetToken, string searchMethod)
        {
            List<GameObject> targets = ManageGameObjectCommon.FindObjectsInternal(targetToken, searchMethod, true);

            if (targets.Count == 0)
            {
                return new ErrorResponse($"Target GameObject(s) ('{targetToken}') not found using method '{searchMethod ?? "default"}'.");
            }

            List<object> deletedObjects = new List<object>();
            foreach (var targetGo in targets)
            {
                if (targetGo != null)
                {
                    string goName = targetGo.name;
                    int goId = targetGo.GetInstanceIDCompat();
                    // Ledger before destroy: GlobalObjectIds are unreadable afterwards. The old
                    // parent's Transform also changes (m_Children loses an entry).
                    SceneMutationLedger.RecordDeletion(targetGo);
                    SceneMutationLedger.Record(targetGo.transform.parent);
                    // Guarded mutations and dry runs roll back through Unity's undo group.
                    // Register the destruction so those operations cannot permanently delete
                    // an object when the transaction is rejected or previewed.
                    Undo.DestroyObjectImmediate(targetGo);
                    deletedObjects.Add(new { name = goName, instanceID = goId });
                }
            }

            if (deletedObjects.Count > 0)
            {
                string message =
                    targets.Count == 1
                        ? $"GameObject '{((dynamic)deletedObjects[0]).name}' deleted successfully."
                        : $"{deletedObjects.Count} GameObjects deleted successfully.";
                return new SuccessResponse(message, deletedObjects);
            }

            return new ErrorResponse("Failed to delete target GameObject(s).");
        }
    }
}
