#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MCPForUnity.Editor.Helpers; // For Response class
using MCPForUnity.Editor.Services.MutationTransactions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MCPForUnity.Editor.Tools.GameObjects
{
    /// <summary>
    /// Handles GameObject manipulation within the current scene (CRUD, find, components).
    /// </summary>
    [McpForUnityTool("manage_gameobject", AutoRegister = false)]
    public static class ManageGameObject
    {
        // --- Main Handler ---

        public static object HandleCommand(JObject @params)
        {
            if (@params == null)
            {
                return new ErrorResponse("Parameters cannot be null.");
            }

            string action = @params["action"]?.ToString().ToLower();
            if (string.IsNullOrEmpty(action))
            {
                return new ErrorResponse("Action parameter is required.");
            }

            // Parameters used by various actions
            JToken targetToken = @params["target"]; // Can be string (name/path) or int (instanceID)
            string name = @params["name"]?.ToString();

            // --- Usability Improvement: Alias 'name' to 'target' for modification actions ---
            // If 'target' is missing but 'name' is provided, and we aren't creating a new object,
            // assume the user meant "find object by name".
            if (targetToken == null && !string.IsNullOrEmpty(name) && action != "create")
            {
                targetToken = name;
                // We don't update @params["target"] because we use targetToken locally mostly,
                // but some downstream methods might parse @params directly. Let's update @params too for safety.
                @params["target"] = name;
            }
            // -------------------------------------------------------------------------------

            string searchMethod = @params["searchMethod"]?.ToString().ToLower();
            string tag = @params["tag"]?.ToString();
            string layer = @params["layer"]?.ToString();
            JToken parentToken = @params["parent"];

            // Coerce string JSON to JObject for 'componentProperties' if provided as a JSON string
            var componentPropsToken = @params["componentProperties"];
            if (componentPropsToken != null && componentPropsToken.Type == JTokenType.String)
            {
                try
                {
                    var parsed = JObject.Parse(componentPropsToken.ToString());
                    @params["componentProperties"] = parsed;
                }
                catch (Exception e)
                {
                    McpLog.Warn($"[ManageGameObject] Could not parse 'componentProperties' JSON string: {e.Message}");
                }
            }

            // --- Prefab Asset Check ---
            // Prefab assets require different tools. Only 'create' (instantiation) is valid here.
            string targetPath =
                targetToken?.Type == JTokenType.String ? targetToken.ToString() : null;
            if (
                !string.IsNullOrEmpty(targetPath)
                && targetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                && action != "create" // Allow prefab instantiation
            )
            {
                return new ErrorResponse(
                    $"Target '{targetPath}' is a prefab asset. " +
                    $"Use 'manage_asset' with action='modify' for prefab asset modifications, " +
                    $"or 'manage_prefabs' with action='modify_contents' to edit the prefab headlessly, or 'manage_prefabs' with action='close_prefab_stage' to exit prefab editing mode."
                );
            }
            // --- End Prefab Asset Check ---

            try
            {
                if (!MutationChangeGuard.TryParse(@params, out MutationChangeGuard guard, out ErrorResponse guardError))
                    return guardError;
                bool dryRun = @params["dryRun"]?.ToObject<bool?>()
                    ?? @params["dry_run"]?.ToObject<bool?>()
                    ?? false;
                if (guard == null && !dryRun)
                    return ExecuteAction(action, @params, targetToken, searchMethod);

                ErrorResponse tagError = ValidateGuardedTagMutation(action, @params);
                if (tagError != null)
                    return tagError;

                var options = new MutationTransactionOptions
                {
                    DirtyScenePolicy = dryRun ? DirtyScenePolicy.Preserve : DirtyScenePolicy.Reject
                };
                string prefabPath = @params["prefabPath"]?.ToString() ?? @params["prefab_path"]?.ToString();
                if (action == "create" && @params["saveAsPrefab"]?.ToObject<bool?>() == true && !string.IsNullOrEmpty(prefabPath))
                {
                    if (!prefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                        prefabPath += ".prefab";
                    options.AdditionalAssetPaths = new[] { prefabPath };
                }

                if (action == "create")
                {
                    GameObject destinationParent = ResolveDestinationParent(@params);
                    Func<object> mutation = () => ExecuteAction(action, @params, targetToken, searchMethod);
                    if (destinationParent != null)
                    {
                        return guard != null
                            ? guard.Execute(new UnityEngine.Object[] { destinationParent }, mutation, options, dryRun)
                            : MutationChangeGuard.ExecutePreview(new UnityEngine.Object[] { destinationParent }, mutation, options);
                    }

                    Scene scene = SceneManager.GetActiveScene();
                    if (!scene.IsValid() || !scene.isLoaded)
                        return new ErrorResponse("TARGET_NOT_FOUND", new { message = "No loaded active scene is available for guarded creation." });
                    return guard != null
                        ? guard.Execute(scene, options, mutation, dryRun)
                        : MutationChangeGuard.ExecutePreview(scene, options, mutation);
                }

                if (action == "delete")
                {
                    List<GameObject> deleteTargets = ManageGameObjectCommon.FindObjectsInternal(
                        targetToken,
                        searchMethod,
                        true);
                    if (deleteTargets.Count == 0)
                        return new ErrorResponse($"Target GameObject(s) ('{targetToken}') not found using method '{searchMethod ?? "default"}'.");
                    Func<object> deleteMutation = () => GameObjectDelete.Handle(deleteTargets, targetToken, searchMethod);
                    return guard != null
                        ? guard.Execute(deleteTargets.Cast<UnityEngine.Object>(), deleteMutation, options, dryRun)
                        : MutationChangeGuard.ExecutePreview(deleteTargets.Cast<UnityEngine.Object>(), deleteMutation, options);
                }

                GameObject target = ManageGameObjectCommon.FindObjectInternal(
                    targetToken,
                    searchMethod,
                    new JObject { ["searchInactive"] = true });
                if (target == null)
                    return new ErrorResponse($"Target GameObject ('{targetToken}') not found using method '{searchMethod ?? "default"}'.");
                AddPrefabRenameAssetPaths(action, @params, target, options);
                var transactionTargets = new List<UnityEngine.Object> { target };
                if (action == "modify" || action == "duplicate")
                {
                    GameObject destinationParent = ResolveDestinationParent(@params);
                    if (destinationParent != null && destinationParent != target)
                        transactionTargets.Add(destinationParent);
                }
                Func<object> targetMutation = () => ExecuteAction(action, @params, targetToken, searchMethod);
                return guard != null
                    ? guard.Execute(transactionTargets, targetMutation, options, dryRun)
                    : MutationChangeGuard.ExecutePreview(transactionTargets, targetMutation, options);
            }
            catch (MutationTransactionException e)
            {
                return new ErrorResponse(e.Code, new
                {
                    message = e.Message,
                    committed = false,
                    rolled_back = e.Code != "ROLLBACK_FAILED"
                });
            }
            catch (Exception e)
            {
                McpLog.Error($"[ManageGameObject] Action '{action}' failed: {e}");
                return new ErrorResponse($"Internal error processing action '{action}': {e.Message}");
            }
        }

        private static ErrorResponse ValidateGuardedTagMutation(string action, JObject @params)
        {
            if (action != "create" && action != "modify")
                return null;

            JToken tagToken = @params["tag"];
            if (tagToken == null)
                return null;
            string requestedTag = tagToken.ToString();
            string effectiveTag = string.IsNullOrEmpty(requestedTag) ? "Untagged" : requestedTag;
            if (effectiveTag == "Untagged" || InternalEditorUtility.tags.Contains(effectiveTag))
                return null;

            return new ErrorResponse("UNTRACKED_PROJECT_SETTINGS_MUTATION", new
            {
                message = $"Tag '{effectiveTag}' does not exist. Guarded operations and dry runs cannot automatically change ProjectSettings/TagManager.asset; create the tag first with manage_editor.",
                tag = effectiveTag,
                committed = false,
                rolled_back = false
            });
        }

        private static GameObject ResolveDestinationParent(JObject @params)
        {
            JToken parentToken = @params["parent"];
            if (parentToken == null || parentToken.Type == JTokenType.Null
                || (parentToken.Type == JTokenType.String && string.IsNullOrEmpty(parentToken.ToString())))
                return null;
            return ManageGameObjectCommon.FindObjectInternal(parentToken, "by_id_or_name_or_path");
        }

        private static void AddPrefabRenameAssetPaths(
            string action,
            JObject @params,
            GameObject target,
            MutationTransactionOptions options)
        {
            if (action != "modify")
                return;
            string requestedName = @params["name"]?.ToString()
                ?? @params["new_name"]?.ToString()
                ?? @params["newName"]?.ToString();
            if (string.IsNullOrEmpty(requestedName))
                return;

            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage == null || prefabStage.prefabContentsRoot != target)
                return;
            string currentPath = AssetPathUtility.NormalizeSeparators(prefabStage.assetPath);
            string directory = System.IO.Path.GetDirectoryName(currentPath);
            string newPath = AssetPathUtility.NormalizeSeparators(
                System.IO.Path.Combine(directory, requestedName + ".prefab"));
            if (string.Equals(currentPath, newPath, StringComparison.Ordinal))
                return;

            options.AdditionalAssetPaths = (options.AdditionalAssetPaths ?? Array.Empty<string>())
                .Concat(new[] { currentPath, newPath })
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        private static object ExecuteAction(string action, JObject @params, JToken targetToken, string searchMethod)
        {
            switch (action)
            {
                case "create": return GameObjectCreate.Handle(@params);
                case "modify": return GameObjectModify.Handle(@params, targetToken, searchMethod);
                case "delete": return GameObjectDelete.Handle(targetToken, searchMethod);
                case "duplicate": return GameObjectDuplicate.Handle(@params, targetToken, searchMethod);
                case "move_relative": return GameObjectMoveRelative.Handle(@params, targetToken, searchMethod);
                case "look_at": return GameObjectLookAt.Handle(@params, targetToken, searchMethod);
                default: return new ErrorResponse($"Unknown action: '{action}'.");
            }
        }
    }
}
