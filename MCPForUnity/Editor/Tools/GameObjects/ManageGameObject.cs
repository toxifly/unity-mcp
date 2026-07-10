#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MCPForUnity.Editor.Helpers; // For Response class
using MCPForUnity.Editor.Services.MutationTransactions;
using Newtonsoft.Json.Linq;
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

                var options = new MutationTransactionOptions
                {
                    DirtyScenePolicy = dryRun ? DirtyScenePolicy.Preserve : DirtyScenePolicy.Reject
                };
                string prefabPath = @params["prefabPath"]?.ToString() ?? @params["prefab_path"]?.ToString();
                if (action == "create" && @params["saveAsPrefab"]?.ToObject<bool?>() == true && !string.IsNullOrEmpty(prefabPath))
                    options.AdditionalAssetPaths = new[] { prefabPath };

                if (action == "create")
                {
                    Scene scene = SceneManager.GetActiveScene();
                    if (!scene.IsValid() || !scene.isLoaded)
                        return new ErrorResponse("TARGET_NOT_FOUND", new { message = "No loaded active scene is available for guarded creation." });
                    Func<object> mutation = () => ExecuteAction(action, @params, targetToken, searchMethod);
                    return guard != null
                        ? guard.Execute(scene, options, mutation, dryRun)
                        : MutationChangeGuard.ExecutePreview(scene, options, mutation);
                }

                GameObject target = ManageGameObjectCommon.FindObjectInternal(
                    targetToken,
                    searchMethod,
                    new JObject { ["searchInactive"] = true });
                if (target == null)
                    return new ErrorResponse($"Target GameObject ('{targetToken}') not found using method '{searchMethod ?? "default"}'.");
                Func<object> targetMutation = () => ExecuteAction(action, @params, targetToken, searchMethod);
                return guard != null
                    ? guard.Execute(new UnityEngine.Object[] { target }, targetMutation, options, dryRun)
                    : MutationChangeGuard.ExecutePreview(new UnityEngine.Object[] { target }, targetMutation, options);
            }
            catch (MutationTransactionException e)
            {
                return new ErrorResponse(e.Code, new { message = e.Message, committed = false, rolled_back = true });
            }
            catch (Exception e)
            {
                McpLog.Error($"[ManageGameObject] Action '{action}' failed: {e}");
                return new ErrorResponse($"Internal error processing action '{action}': {e.Message}");
            }
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
