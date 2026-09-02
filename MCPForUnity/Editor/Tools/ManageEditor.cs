using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Tools.Build;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditorInternal; // Required for tag management
using UnityEngine;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Handles editor control actions including play mode control, tool selection,
    /// and tag/layer management. For reading editor state, use MCP resources instead.
    /// </summary>
    [McpForUnityTool("manage_editor", AutoRegister = false)]
    public static class ManageEditor
    {
        // Constant for starting user layer index
        private const int FirstUserLayerIndex = 8;

        // Constant for total layer count
        private const int TotalLayerCount = 32;

        /// <summary>
        /// Main handler for editor management actions.
        /// </summary>
        public static object HandleCommand(JObject @params)
        {
            // Step 1: Null parameter guard (consistent across all tools)
            if (@params == null)
            {
                return new ErrorResponse("Parameters cannot be null.");
            }

            // Step 2: Wrap parameters
            var p = new ToolParams(@params);

            // Step 3: Extract and validate required parameters
            var actionResult = p.GetRequired("action");
            if (!actionResult.IsSuccess)
            {
                return new ErrorResponse(actionResult.ErrorMessage);
            }
            string action = actionResult.Value.ToLowerInvariant();

            // Parameters for specific actions
            string tagName = p.Get("tagName");
            string layerName = p.Get("layerName");
            // Route action
            switch (action)
            {
                // Play Mode Control
                case "play":
                    try
                    {
                        if (!EditorApplication.isPlaying)
                        {
                            EditorApplication.isPlaying = true;
                            return new SuccessResponse("Entered play mode.");
                        }
                        return new SuccessResponse("Already in play mode.");
                    }
                    catch (Exception e)
                    {
                        return new ErrorResponse($"Error entering play mode: {e.Message}");
                    }
                case "pause":
                    try
                    {
                        if (EditorApplication.isPlaying)
                        {
                            EditorApplication.isPaused = !EditorApplication.isPaused;
                            return new SuccessResponse(
                                EditorApplication.isPaused ? "Game paused." : "Game resumed."
                            );
                        }
                        return new ErrorResponse("Cannot pause/resume: Not in play mode.");
                    }
                    catch (Exception e)
                    {
                        return new ErrorResponse($"Error pausing/resuming game: {e.Message}");
                    }
                case "stop":
                    try
                    {
                        if (EditorApplication.isPlaying)
                        {
                            EditorApplication.isPlaying = false;
                            return new SuccessResponse("Exited play mode.");
                        }
                        return new SuccessResponse("Already stopped (not in play mode).");
                    }
                    catch (Exception e)
                    {
                        return new ErrorResponse($"Error stopping play mode: {e.Message}");
                    }

                // Tool Control
                case "set_active_tool":
                    var toolNameResult = p.GetRequired("toolName", "'toolName' parameter required for set_active_tool.");
                    if (!toolNameResult.IsSuccess)
                        return new ErrorResponse(toolNameResult.ErrorMessage);
                    return SetActiveTool(toolNameResult.Value);

                // Tag Management
                case "add_tag":
                    var addTagResult = p.GetRequired("tagName", "'tagName' parameter required for add_tag.");
                    if (!addTagResult.IsSuccess)
                        return new ErrorResponse(addTagResult.ErrorMessage);
                    return AddTag(addTagResult.Value);
                case "remove_tag":
                    var removeTagResult = p.GetRequired("tagName", "'tagName' parameter required for remove_tag.");
                    if (!removeTagResult.IsSuccess)
                        return new ErrorResponse(removeTagResult.ErrorMessage);
                    return RemoveTag(removeTagResult.Value);
                // Layer Management
                case "add_layer":
                    var addLayerResult = p.GetRequired("layerName", "'layerName' parameter required for add_layer.");
                    if (!addLayerResult.IsSuccess)
                        return new ErrorResponse(addLayerResult.ErrorMessage);
                    return AddLayer(addLayerResult.Value);
                case "remove_layer":
                    var removeLayerResult = p.GetRequired("layerName", "'layerName' parameter required for remove_layer.");
                    if (!removeLayerResult.IsSuccess)
                        return new ErrorResponse(removeLayerResult.ErrorMessage);
                    return RemoveLayer(removeLayerResult.Value);
                // --- Settings (Example) ---
                // case "set_resolution":
                //     int? width = @params["width"]?.ToObject<int?>();
                //     int? height = @params["height"]?.ToObject<int?>();
                //     if (!width.HasValue || !height.HasValue) return new ErrorResponse("'width' and 'height' parameters required.");
                //     return SetGameViewResolution(width.Value, height.Value);
                // case "set_quality":
                //     // Handle string name or int index
                //     return SetQualityLevel(@params["qualityLevel"]);

                // Scripting Defines
                case "get_scripting_defines":
                    return GetScriptingDefines(p.Get("target"));
                case "set_scripting_defines":
                    return SetScriptingDefines(p.Get("target"), p.GetRaw("defines"));

                // Package Deployment
                case "deploy_package":
                    return DeployPackage();
                case "restore_package":
                    return RestorePackage();

                // Undo/Redo
                case "undo":
                {
                    string groupName = Undo.GetCurrentGroupName();
                    Undo.PerformUndo();
                    string message = string.IsNullOrEmpty(groupName)
                        ? "Undo performed (stack may be empty)."
                        : $"Undid: {groupName}";
                    if (EditorApplication.isPlaying)
                        message += " Warning: undo during play mode may have unexpected effects.";
                    return new SuccessResponse(message, new
                    {
                        undone_group = string.IsNullOrEmpty(groupName) ? (string)null : groupName,
                        next_group = Undo.GetCurrentGroupName()
                    });
                }
                case "redo":
                {
                    Undo.PerformRedo();
                    string nextGroup = Undo.GetCurrentGroupName();
                    string message = "Redo performed.";
                    if (EditorApplication.isPlaying)
                        message += " Warning: redo during play mode may have unexpected effects.";
                    return new SuccessResponse(message, new
                    {
                        current_group = string.IsNullOrEmpty(nextGroup) ? (string)null : nextGroup
                    });
                }

                default:
                    return new ErrorResponse(
                        $"Unknown action: '{action}'. Supported actions: play, pause, stop, set_active_tool, add_tag, remove_tag, add_layer, remove_layer, get_scripting_defines, set_scripting_defines, deploy_package, restore_package, undo, redo. For prefab editing (open/save/close prefab stage), use manage_prefabs. Use MCP resources for reading editor state, project info, tags, layers, selection, windows, prefab stage, and active tool."
                    );
            }
        }

        // --- Tool Control Methods ---

        private static object SetActiveTool(string toolName)
        {
            try
            {
                Tool targetTool;
                if (Enum.TryParse<Tool>(toolName, true, out targetTool)) // Case-insensitive parse
                {
                    // Check if it's a valid built-in tool
                    if (targetTool != Tool.None && targetTool <= Tool.Custom) // Tool.Custom is the last standard tool
                    {
                        UnityEditor.Tools.current = targetTool;
                        return new SuccessResponse($"Set active tool to '{targetTool}'.");
                    }
                    else
                    {
                        return new ErrorResponse(
                            $"Cannot directly set tool to '{toolName}'. It might be None, Custom, or invalid."
                        );
                    }
                }
                else
                {
                    // Potentially try activating a custom tool by name here if needed
                    // This often requires specific editor scripting knowledge for that tool.
                    return new ErrorResponse(
                        $"Could not parse '{toolName}' as a standard Unity Tool (View, Move, Rotate, Scale, Rect, Transform, Custom)."
                    );
                }
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Error setting active tool: {e.Message}");
            }
        }

        // --- Tag Management Methods ---

        private static object AddTag(string tagName)
        {
            if (string.IsNullOrWhiteSpace(tagName))
                return new ErrorResponse("Tag name cannot be empty or whitespace.");

            // Check if tag already exists
            if (System.Linq.Enumerable.Contains(InternalEditorUtility.tags, tagName))
            {
                return new ErrorResponse($"Tag '{tagName}' already exists.");
            }

            try
            {
                // Add the tag using the internal utility
                InternalEditorUtility.AddTag(tagName);
                // Force save assets to ensure the change persists in the TagManager asset
                AssetDatabase.SaveAssets();
                return new SuccessResponse($"Tag '{tagName}' added successfully.");
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Failed to add tag '{tagName}': {e.Message}");
            }
        }

        private static object RemoveTag(string tagName)
        {
            if (string.IsNullOrWhiteSpace(tagName))
                return new ErrorResponse("Tag name cannot be empty or whitespace.");
            if (tagName.Equals("Untagged", StringComparison.OrdinalIgnoreCase))
                return new ErrorResponse("Cannot remove the built-in 'Untagged' tag.");

            // Check if tag exists before attempting removal
            if (!System.Linq.Enumerable.Contains(InternalEditorUtility.tags, tagName))
            {
                return new ErrorResponse($"Tag '{tagName}' does not exist.");
            }

            try
            {
                // Remove the tag using the internal utility
                InternalEditorUtility.RemoveTag(tagName);
                // Force save assets
                AssetDatabase.SaveAssets();
                return new SuccessResponse($"Tag '{tagName}' removed successfully.");
            }
            catch (Exception e)
            {
                // Catch potential issues if the tag is somehow in use or removal fails
                return new ErrorResponse($"Failed to remove tag '{tagName}': {e.Message}");
            }
        }

        // --- Layer Management Methods ---

        private static object AddLayer(string layerName)
        {
            if (string.IsNullOrWhiteSpace(layerName))
                return new ErrorResponse("Layer name cannot be empty or whitespace.");

            // Access the TagManager asset
            SerializedObject tagManager = GetTagManager();
            if (tagManager == null)
                return new ErrorResponse("Could not access TagManager asset.");

            SerializedProperty layersProp = tagManager.FindProperty("layers");
            if (layersProp == null || !layersProp.isArray)
                return new ErrorResponse("Could not find 'layers' property in TagManager.");

            // Check if layer name already exists (case-insensitive check recommended)
            for (int i = 0; i < TotalLayerCount; i++)
            {
                SerializedProperty layerSP = layersProp.GetArrayElementAtIndex(i);
                if (
                    layerSP != null
                    && layerName.Equals(layerSP.stringValue, StringComparison.OrdinalIgnoreCase)
                )
                {
                    return new ErrorResponse($"Layer '{layerName}' already exists at index {i}.");
                }
            }

            // Find the first empty user layer slot (indices 8 to 31)
            int firstEmptyUserLayer = -1;
            for (int i = FirstUserLayerIndex; i < TotalLayerCount; i++)
            {
                SerializedProperty layerSP = layersProp.GetArrayElementAtIndex(i);
                if (layerSP != null && string.IsNullOrEmpty(layerSP.stringValue))
                {
                    firstEmptyUserLayer = i;
                    break;
                }
            }

            if (firstEmptyUserLayer == -1)
            {
                return new ErrorResponse("No empty User Layer slots available (8-31 are full).");
            }

            // Assign the name to the found slot
            try
            {
                SerializedProperty targetLayerSP = layersProp.GetArrayElementAtIndex(
                    firstEmptyUserLayer
                );
                targetLayerSP.stringValue = layerName;
                // Apply the changes to the TagManager asset
                tagManager.ApplyModifiedProperties();
                // Save assets to make sure it's written to disk
                AssetDatabase.SaveAssets();
                return new SuccessResponse(
                    $"Layer '{layerName}' added successfully to slot {firstEmptyUserLayer}."
                );
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Failed to add layer '{layerName}': {e.Message}");
            }
        }

        private static object RemoveLayer(string layerName)
        {
            if (string.IsNullOrWhiteSpace(layerName))
                return new ErrorResponse("Layer name cannot be empty or whitespace.");

            // Access the TagManager asset
            SerializedObject tagManager = GetTagManager();
            if (tagManager == null)
                return new ErrorResponse("Could not access TagManager asset.");

            SerializedProperty layersProp = tagManager.FindProperty("layers");
            if (layersProp == null || !layersProp.isArray)
                return new ErrorResponse("Could not find 'layers' property in TagManager.");

            // Find the layer by name (must be user layer)
            int layerIndexToRemove = -1;
            for (int i = FirstUserLayerIndex; i < TotalLayerCount; i++) // Start from user layers
            {
                SerializedProperty layerSP = layersProp.GetArrayElementAtIndex(i);
                // Case-insensitive comparison is safer
                if (
                    layerSP != null
                    && layerName.Equals(layerSP.stringValue, StringComparison.OrdinalIgnoreCase)
                )
                {
                    layerIndexToRemove = i;
                    break;
                }
            }

            if (layerIndexToRemove == -1)
            {
                return new ErrorResponse($"User layer '{layerName}' not found.");
            }

            // Clear the name for that index
            try
            {
                SerializedProperty targetLayerSP = layersProp.GetArrayElementAtIndex(
                    layerIndexToRemove
                );
                targetLayerSP.stringValue = string.Empty; // Set to empty string to remove
                // Apply the changes
                tagManager.ApplyModifiedProperties();
                // Save assets
                AssetDatabase.SaveAssets();
                return new SuccessResponse(
                    $"Layer '{layerName}' (slot {layerIndexToRemove}) removed successfully."
                );
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Failed to remove layer '{layerName}': {e.Message}");
            }
        }

        // --- Scripting Define Methods ---

        private static object GetScriptingDefines(string targetName)
        {
            string targetError = BuildTargetMapping.TryResolveNamedBuildTarget(targetName, out var namedTarget);
            if (targetError != null)
                return new ErrorResponse(targetError);

            string raw = PlayerSettings.GetScriptingDefineSymbols(namedTarget);
            return new SuccessResponse($"Read scripting defines for '{namedTarget.TargetName}'.", new
            {
                target = namedTarget.TargetName,
                defines = SplitDefines(raw),
                raw
            });
        }

        private static object SetScriptingDefines(string targetName, JToken definesToken)
        {
            if (definesToken == null || definesToken.Type == JTokenType.Undefined)
                return new ErrorResponse("'defines' parameter required for set_scripting_defines. Pass an empty list to clear.");

            // Rewriting defines recompiles every script and reloads the domain, which races a play
            // session or an in-flight test run the same way a forced refresh does.
            if (TestRunStatus.IsRunning)
            {
                return new ErrorResponse("tests_running", new { reason = "tests_running", retry_after_ms = 5000 });
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return new ErrorResponse("play_mode_active", new
                {
                    reason = "play_mode_active",
                    message = "Refusing to change scripting defines while the editor is playing; the "
                              + "domain reload can wedge play-exit. Stop Play Mode (manage_editor action=stop), then retry."
                });
            }

            string targetError = BuildTargetMapping.TryResolveNamedBuildTarget(targetName, out var namedTarget);
            if (targetError != null)
                return new ErrorResponse(targetError);

            string parseError = NormalizeDefines(definesToken, out var defines);
            if (parseError != null)
                return new ErrorResponse(parseError);

            string previousRaw = PlayerSettings.GetScriptingDefineSymbols(namedTarget);
            string raw = string.Join(";", defines);
            if (string.Equals(previousRaw, raw, StringComparison.Ordinal))
            {
                return new SuccessResponse(
                    $"Scripting defines for '{namedTarget.TargetName}' already match; nothing to do.",
                    new
                    {
                        target = namedTarget.TargetName,
                        defines,
                        raw,
                        previous = SplitDefines(previousRaw),
                        changed = false
                    });
            }

            try
            {
                PlayerSettings.SetScriptingDefineSymbols(namedTarget, raw);
                AssetDatabase.SaveAssets();
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Failed to set scripting defines: {e.Message}");
            }

            return new SuccessResponse($"Set scripting defines for '{namedTarget.TargetName}'.", new
            {
                target = namedTarget.TargetName,
                defines,
                raw,
                previous = SplitDefines(previousRaw),
                changed = true,
                hint = "Unity will recompile; poll editor_state or call refresh_unity(wait_for_ready=true)."
            });
        }

        internal static string[] SplitDefines(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return Array.Empty<string>();

            return raw
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(symbol => symbol.Trim())
                .Where(symbol => symbol.Length > 0)
                .ToArray();
        }

        /// <summary>
        /// Accepts a JSON array or a ';'/','-separated string, and yields trimmed, deduplicated
        /// symbols in the order given. Returns an error message when an entry is not a usable symbol.
        /// </summary>
        internal static string NormalizeDefines(JToken token, out string[] defines)
        {
            defines = Array.Empty<string>();

            IEnumerable<string> raw;
            if (token.Type == JTokenType.Array)
            {
                raw = ((JArray)token).Select(entry => entry?.ToString());
            }
            else if (token.Type == JTokenType.Null)
            {
                raw = Array.Empty<string>();
            }
            else
            {
                // Clients serialize lists inconsistently, so accept a stringified JSON array
                // as well as the ';'-separated form Unity itself stores.
                string text = token.ToString().Trim();
                if (text.StartsWith("[", StringComparison.Ordinal) && text.EndsWith("]", StringComparison.Ordinal))
                {
                    try
                    {
                        raw = JArray.Parse(text).Select(entry => entry?.ToString()).ToArray();
                    }
                    catch (JsonException)
                    {
                        return $"Invalid scripting defines '{text}': expected a JSON array of symbols.";
                    }
                }
                else
                {
                    raw = text.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
                }
            }

            var ordered = new List<string>();
            foreach (var entry in raw)
            {
                string symbol = entry?.Trim();
                if (string.IsNullOrEmpty(symbol))
                    continue;
                if (!IsValidConditionalCompilationSymbol(symbol))
                {
                    return $"Invalid scripting define '{entry}': symbols must be valid C# "
                           + "conditional-compilation identifiers (a letter or '_' first, followed "
                           + "by identifier characters), and cannot be 'true' or 'false'.";
                }
                if (!ordered.Contains(symbol, StringComparer.Ordinal))
                    ordered.Add(symbol);
            }

            defines = ordered.ToArray();
            return null;
        }

        private static bool IsValidConditionalCompilationSymbol(string symbol)
        {
            // C#'s PP_Conditional_Symbol grammar is Basic_Identifier, except for the
            // reserved tokens true and false. Walk Unicode code points so non-ASCII C#
            // identifiers remain valid without accidentally accepting punctuation or a
            // delimiter embedded in an array entry.
            if (string.Equals(symbol, "true", StringComparison.Ordinal)
                || string.Equals(symbol, "false", StringComparison.Ordinal))
            {
                return false;
            }

            for (int index = 0; index < symbol.Length;)
            {
                UnicodeCategory category;
                try
                {
                    category = CharUnicodeInfo.GetUnicodeCategory(symbol, index);
                }
                catch (ArgumentException)
                {
                    return false;
                }

                bool isFirst = index == 0;
                bool isLetter = category == UnicodeCategory.UppercaseLetter
                                || category == UnicodeCategory.LowercaseLetter
                                || category == UnicodeCategory.TitlecaseLetter
                                || category == UnicodeCategory.ModifierLetter
                                || category == UnicodeCategory.OtherLetter
                                || category == UnicodeCategory.LetterNumber;
                bool isPart = isLetter
                              || category == UnicodeCategory.DecimalDigitNumber
                              || category == UnicodeCategory.ConnectorPunctuation
                              || category == UnicodeCategory.NonSpacingMark
                              || category == UnicodeCategory.SpacingCombiningMark
                              || category == UnicodeCategory.Format;

                if (isFirst ? symbol[index] != '_' && !isLetter : !isPart)
                {
                    return false;
                }

                index += char.IsHighSurrogate(symbol[index])
                         && index + 1 < symbol.Length
                         && char.IsLowSurrogate(symbol[index + 1]) ? 2 : 1;
            }

            return symbol.Length > 0;
        }

        // --- Package Deployment Methods ---

        private static object DeployPackage()
        {
            try
            {
                var result = MCPServiceLocator.Deployment.DeployFromStoredSource();
                if (!result.Success)
                    return new ErrorResponse(result.Message);

                return new SuccessResponse(result.Message, new
                {
                    source_path = result.SourcePath,
                    target_path = result.TargetPath,
                    backup_path = result.BackupPath
                });
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Deploy failed: {e.Message}");
            }
        }

        private static object RestorePackage()
        {
            try
            {
                var result = MCPServiceLocator.Deployment.RestoreLastBackup();
                if (!result.Success)
                    return new ErrorResponse(result.Message);

                return new SuccessResponse(result.Message, new
                {
                    target_path = result.TargetPath,
                    backup_path = result.BackupPath
                });
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Restore failed: {e.Message}");
            }
        }

        // --- Helper Methods ---

        /// <summary>
        /// Gets the SerializedObject for the TagManager asset.
        /// </summary>
        private static SerializedObject GetTagManager()
        {
            try
            {
                // Load the TagManager asset from the ProjectSettings folder
                UnityEngine.Object[] tagManagerAssets = AssetDatabase.LoadAllAssetsAtPath(
                    "ProjectSettings/TagManager.asset"
                );
                if (tagManagerAssets == null || tagManagerAssets.Length == 0)
                {
                    McpLog.Error("[ManageEditor] TagManager.asset not found in ProjectSettings.");
                    return null;
                }
                // The first object in the asset file should be the TagManager
                return new SerializedObject(tagManagerAssets[0]);
            }
            catch (Exception e)
            {
                McpLog.Error($"[ManageEditor] Error accessing TagManager.asset: {e.Message}");
                return null;
            }
        }

        // --- Example Implementations for Settings ---
        /*
        private static object SetGameViewResolution(int width, int height) { ... }
        private static object SetQualityLevel(JToken qualityLevelToken) { ... }
        */
    }
}
