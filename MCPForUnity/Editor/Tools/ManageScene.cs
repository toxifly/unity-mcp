using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers; // For Response class
using MCPForUnity.Runtime.Helpers; // For ScreenshotUtility
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Handles scene management operations like loading, saving, creating, and querying hierarchy.
    /// </summary>
    [McpForUnityTool("manage_scene", AutoRegister = false)]
    public static class ManageScene
    {
        private const double DefaultAsyncScreenshotImportTimeoutSeconds = 30d;

        private sealed class SceneCommand
        {
            public string action { get; set; } = string.Empty;
            public string name { get; set; } = string.Empty;
            public string path { get; set; } = string.Empty;
            public int? buildIndex { get; set; }
            public string fileName { get; set; } = string.Empty;
            public int? superSize { get; set; }
            public int? width { get; set; }
            public int? height { get; set; }
            public bool? returnPreview { get; set; }
            public string returnMode { get; set; } = string.Empty;
            public int? previewMaxWidth { get; set; }
            public int? previewMaxHeight { get; set; }
            public string previewFormat { get; set; } = string.Empty;
            public int? previewJpegQuality { get; set; }
            public int? previewMaxPixels { get; set; }
            public bool? waitForWrite { get; set; }
            public int? timeoutMs { get; set; }

            // get_hierarchy paging + safety (summary-first)
            public JToken parent { get; set; }
            public int? pageSize { get; set; }
            public int? cursor { get; set; }
            public int? maxNodes { get; set; }
            public int? maxDepth { get; set; }
            public int? maxChildrenPerNode { get; set; }
            public bool? includeTransform { get; set; }
        }

        private sealed class ScreenshotRequestOptions
        {
            public string ReturnMode { get; set; } = "path"; // path | preview | both
            public bool WaitForWrite { get; set; }
            public int TimeoutMs { get; set; } = 5000;
            public int PreviewMaxWidth { get; set; } = 960;
            public int PreviewMaxHeight { get; set; } = 540;
            public string PreviewFormat { get; set; } = "jpg"; // jpg | png
            public int PreviewJpegQuality { get; set; } = 70;
            public int PreviewMaxPixels { get; set; } = 600000;
            public int? CaptureWidth { get; set; }
            public int? CaptureHeight { get; set; }

            public bool WantsPreview =>
                string.Equals(ReturnMode, "preview", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ReturnMode, "both", StringComparison.OrdinalIgnoreCase);

            public bool HasCaptureSizeOverride => CaptureWidth.HasValue || CaptureHeight.HasValue;

            public bool RequiresImmediateCapture => WaitForWrite || WantsPreview || HasCaptureSizeOverride;
        }

        private static SceneCommand ToSceneCommand(JObject p)
        {
            if (p == null) return new SceneCommand();
            return new SceneCommand
            {
                action = (p["action"]?.ToString() ?? string.Empty).Trim().ToLowerInvariant(),
                name = p["name"]?.ToString() ?? string.Empty,
                path = p["path"]?.ToString() ?? string.Empty,
                buildIndex = ParamCoercion.CoerceIntNullable(p["buildIndex"] ?? p["build_index"]),
                fileName = (p["fileName"] ?? p["filename"])?.ToString() ?? string.Empty,
                superSize = ParamCoercion.CoerceIntNullable(p["superSize"] ?? p["super_size"] ?? p["supersize"]),
                width = ParamCoercion.CoerceIntNullable(p["width"] ?? p["captureWidth"] ?? p["capture_width"]),
                height = ParamCoercion.CoerceIntNullable(p["height"] ?? p["captureHeight"] ?? p["capture_height"]),
                returnPreview = ParamCoercion.CoerceBoolNullable(p["returnPreview"] ?? p["return_preview"]),
                returnMode = (p["returnMode"] ?? p["return_mode"])?.ToString() ?? string.Empty,
                previewMaxWidth = ParamCoercion.CoerceIntNullable(p["previewMaxWidth"] ?? p["preview_max_width"]),
                previewMaxHeight = ParamCoercion.CoerceIntNullable(p["previewMaxHeight"] ?? p["preview_max_height"]),
                previewFormat = (p["previewFormat"] ?? p["preview_format"])?.ToString() ?? string.Empty,
                previewJpegQuality = ParamCoercion.CoerceIntNullable(p["previewJpegQuality"] ?? p["preview_jpeg_quality"]),
                previewMaxPixels = ParamCoercion.CoerceIntNullable(p["previewMaxPixels"] ?? p["preview_max_pixels"]),
                waitForWrite = ParamCoercion.CoerceBoolNullable(p["waitForWrite"] ?? p["wait_for_write"]),
                timeoutMs = ParamCoercion.CoerceIntNullable(p["timeoutMs"] ?? p["timeout_ms"]),

                // get_hierarchy paging + safety
                parent = p["parent"],
                pageSize = ParamCoercion.CoerceIntNullable(p["pageSize"] ?? p["page_size"]),
                cursor = ParamCoercion.CoerceIntNullable(p["cursor"]),
                maxNodes = ParamCoercion.CoerceIntNullable(p["maxNodes"] ?? p["max_nodes"]),
                maxDepth = ParamCoercion.CoerceIntNullable(p["maxDepth"] ?? p["max_depth"]),
                maxChildrenPerNode = ParamCoercion.CoerceIntNullable(p["maxChildrenPerNode"] ?? p["max_children_per_node"]),
                includeTransform = ParamCoercion.CoerceBoolNullable(p["includeTransform"] ?? p["include_transform"]),
            };
        }

        /// <summary>
        /// Main handler for scene management actions.
        /// </summary>
        public static object HandleCommand(JObject @params)
        {
            try { McpLog.Info("[ManageScene] HandleCommand: start", always: false); } catch { }
            var cmd = ToSceneCommand(@params);
            string action = cmd.action;
            string name = string.IsNullOrEmpty(cmd.name) ? null : cmd.name;
            string path = string.IsNullOrEmpty(cmd.path) ? null : cmd.path; // Relative to Assets/
            int? buildIndex = cmd.buildIndex;
            // bool loadAdditive = @params["loadAdditive"]?.ToObject<bool>() ?? false; // Example for future extension

            // --- Path normalization ---
            // `path` may be provided as:
            //  - A full scene file path: "Assets/Scenes/MyScene.unity"
            //  - A folder path under Assets: "Assets/Scenes" (combined with `name`)
            //  - A folder path relative to Assets: "Scenes" (combined with `name`)
            //
            // Historically this tool treated `path` as a folder, which could accidentally create a folder
            // ending with ".unity" when callers passed a full file path. Detect and handle file paths.
            bool pathIsSceneFile = false;
            string relativeDir = string.Empty;   // relative to Assets/ (no leading "Assets/")
            string sceneFileName = null;         // ends with .unity when available
            string relativePath = null;          // always starts with "Assets/" when available

            string assetsRelative = path ?? string.Empty;
            if (!string.IsNullOrEmpty(assetsRelative))
            {
                assetsRelative = AssetPathUtility.NormalizeSeparators(assetsRelative).Trim('/');
                if (assetsRelative.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                {
                    assetsRelative = assetsRelative.Substring("Assets/".Length).TrimStart('/');
                }

                if (assetsRelative.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                {
                    pathIsSceneFile = true;
                    sceneFileName = Path.GetFileName(assetsRelative);
                    relativeDir = Path.GetDirectoryName(assetsRelative) ?? string.Empty;
                    relativeDir = relativeDir.Replace('\\', '/');
                    relativePath = Path.Combine("Assets", assetsRelative).Replace('\\', '/');

                    // If `name` was provided but doesn't match the file name, prefer the file name.
                    string derivedName = Path.GetFileNameWithoutExtension(sceneFileName);
                    if (string.IsNullOrEmpty(name) || !string.Equals(name, derivedName, StringComparison.OrdinalIgnoreCase))
                    {
                        name = derivedName;
                    }
                }
                else
                {
                    // Treat as folder path under Assets/
                    relativeDir = assetsRelative;
                }
            }

            // Apply default *after* sanitizing, using the original path variable for the check
            if (string.IsNullOrEmpty(path) && action == "create") // Check original path for emptiness
            {
                relativeDir = "Scenes"; // Default relative directory
            }

            if (string.IsNullOrEmpty(action))
            {
                return new ErrorResponse("Action parameter is required.");
            }

            if (!pathIsSceneFile)
            {
                sceneFileName = string.IsNullOrEmpty(name) ? null : $"{name}.unity";
                // Ensure relativePath always starts with "Assets/" and uses forward slashes
                relativePath = string.IsNullOrEmpty(sceneFileName)
                    ? null
                    : Path.Combine("Assets", relativeDir, sceneFileName).Replace('\\', '/');
            }

            // Construct full system path correctly: ProjectRoot/Assets/relativeDir/sceneFileName
            string fullPathDir = Path.Combine(Application.dataPath, relativeDir); // Application.dataPath ends in Assets
            string fullPath = string.IsNullOrEmpty(sceneFileName)
                ? null
                : Path.Combine(fullPathDir, sceneFileName);
            // Ensure relativePath uses forward slashes
            if (relativePath != null)
            {
                relativePath = AssetPathUtility.NormalizeSeparators(relativePath);
            }

            // Ensure directory exists for 'create'
            if (action == "create" && !string.IsNullOrEmpty(fullPathDir))
            {
                try
                {
                    Directory.CreateDirectory(fullPathDir);
                }
                catch (Exception e)
                {
                    return new ErrorResponse(
                        $"Could not create directory '{fullPathDir}': {e.Message}"
                    );
                }
            }

            // Route action
            try { McpLog.Info($"[ManageScene] Route action='{action}' name='{name}' path='{path}' buildIndex={(buildIndex.HasValue ? buildIndex.Value.ToString() : "null")}", always: false); } catch { }
            switch (action)
            {
                case "create":
                    if (string.IsNullOrEmpty(relativePath))
                        return new ErrorResponse(
                            "Scene path is required for 'create'. Provide either a full '.unity' file path in 'path' (e.g. Assets/Scenes/MyScene.unity) or a folder 'path' plus 'name'."
                        );
                    return CreateScene(fullPath, relativePath);
                case "load":
                    // Loading can be done by path/name or build index
                    if (!string.IsNullOrEmpty(relativePath))
                        return LoadScene(relativePath);
                    else if (buildIndex.HasValue)
                        return LoadScene(buildIndex.Value);
                    else
                        return new ErrorResponse(
                            "Either 'name'/'path' or 'buildIndex' must be provided for 'load' action."
                        );
                case "save":
                    // Save current scene, optionally to a new path
                    return SaveScene(fullPath, relativePath);
                case "get_hierarchy":
                    try { McpLog.Info("[ManageScene] get_hierarchy: entering", always: false); } catch { }
                    var gh = GetSceneHierarchyPaged(cmd);
                    try { McpLog.Info("[ManageScene] get_hierarchy: exiting", always: false); } catch { }
                    return gh;
                case "get_active":
                    try { McpLog.Info("[ManageScene] get_active: entering", always: false); } catch { }
                    var ga = GetActiveSceneInfo();
                    try { McpLog.Info("[ManageScene] get_active: exiting", always: false); } catch { }
                    return ga;
                case "get_build_settings":
                    return GetBuildSettingsScenes();
                case "screenshot":
                    return CaptureScreenshot(cmd, previewModeDefault: false);
                case "screenshot_with_preview":
                case "screenshot_preview":
                    return CaptureScreenshot(cmd, previewModeDefault: true);
                // Add cases for modifying build settings, additive loading, unloading etc.
                default:
                    return new ErrorResponse(
                        $"Unknown action: '{action}'. Valid actions: create, load, save, get_hierarchy, get_active, get_build_settings, screenshot, screenshot_with_preview, screenshot_preview."
                    );
            }
        }

        /// <summary>
        /// Captures a screenshot to Assets/Screenshots and returns a response payload.
        /// Public so the tools UI can reuse the same logic without duplicating parameters.
        /// Available in both Edit Mode and Play Mode.
        /// </summary>
        public static object ExecuteScreenshot(string fileName = null, int? superSize = null)
        {
            return CaptureScreenshot(
                new SceneCommand
                {
                    fileName = fileName ?? string.Empty,
                    superSize = superSize,
                },
                previewModeDefault: false
            );
        }

        private static object CreateScene(string fullPath, string relativePath)
        {
            if (File.Exists(fullPath))
            {
                return new ErrorResponse($"Scene already exists at '{relativePath}'.");
            }

            try
            {
                // Create a new empty scene
                Scene newScene = EditorSceneManager.NewScene(
                    NewSceneSetup.EmptyScene,
                    NewSceneMode.Single
                );
                // Save it to the specified path
                bool saved = EditorSceneManager.SaveScene(newScene, relativePath);

                if (saved)
                {
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport); // Ensure Unity sees the new scene file
                    return new SuccessResponse(
                        $"Scene '{Path.GetFileName(relativePath)}' created successfully at '{relativePath}'.",
                        new { path = relativePath }
                    );
                }
                else
                {
                    // If SaveScene fails, it might leave an untitled scene open.
                    // Optionally try to close it, but be cautious.
                    return new ErrorResponse($"Failed to save new scene to '{relativePath}'.");
                }
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Error creating scene '{relativePath}': {e.Message}");
            }
        }

        private static object LoadScene(string relativePath)
        {
            if (
                !File.Exists(
                    Path.Combine(
                        Application.dataPath.Substring(
                            0,
                            Application.dataPath.Length - "Assets".Length
                        ),
                        relativePath
                    )
                )
            )
            {
                return new ErrorResponse($"Scene file not found at '{relativePath}'.");
            }

            // Check for unsaved changes in the current scene
            if (EditorSceneManager.GetActiveScene().isDirty)
            {
                // Optionally prompt the user or save automatically before loading
                return new ErrorResponse(
                    "Current scene has unsaved changes. Please save or discard changes before loading a new scene."
                );
                // Example: bool saveOK = EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo();
                // if (!saveOK) return new ErrorResponse("Load cancelled by user.");
            }

            try
            {
                EditorSceneManager.OpenScene(relativePath, OpenSceneMode.Single);
                return new SuccessResponse(
                    $"Scene '{relativePath}' loaded successfully.",
                    new
                    {
                        path = relativePath,
                        name = Path.GetFileNameWithoutExtension(relativePath),
                    }
                );
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Error loading scene '{relativePath}': {e.Message}");
            }
        }

        private static object LoadScene(int buildIndex)
        {
            if (buildIndex < 0 || buildIndex >= SceneManager.sceneCountInBuildSettings)
            {
                return new ErrorResponse(
                    $"Invalid build index: {buildIndex}. Must be between 0 and {SceneManager.sceneCountInBuildSettings - 1}."
                );
            }

            // Check for unsaved changes
            if (EditorSceneManager.GetActiveScene().isDirty)
            {
                return new ErrorResponse(
                    "Current scene has unsaved changes. Please save or discard changes before loading a new scene."
                );
            }

            try
            {
                string scenePath = SceneUtility.GetScenePathByBuildIndex(buildIndex);
                EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                return new SuccessResponse(
                    $"Scene at build index {buildIndex} ('{scenePath}') loaded successfully.",
                    new
                    {
                        path = scenePath,
                        name = Path.GetFileNameWithoutExtension(scenePath),
                        buildIndex = buildIndex,
                    }
                );
            }
            catch (Exception e)
            {
                return new ErrorResponse(
                    $"Error loading scene with build index {buildIndex}: {e.Message}"
                );
            }
        }

        private static object SaveScene(string fullPath, string relativePath)
        {
            try
            {
                Scene currentScene = EditorSceneManager.GetActiveScene();
                if (!currentScene.IsValid())
                {
                    return new ErrorResponse("No valid scene is currently active to save.");
                }

                bool saved;
                string finalPath = currentScene.path; // Path where it was last saved or will be saved

                if (!string.IsNullOrEmpty(relativePath) && currentScene.path != relativePath)
                {
                    // Save As...
                    // Ensure directory exists
                    string dir = Path.GetDirectoryName(fullPath);
                    if (!Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    saved = EditorSceneManager.SaveScene(currentScene, relativePath);
                    finalPath = relativePath;
                }
                else
                {
                    // Save (overwrite existing or save untitled)
                    if (string.IsNullOrEmpty(currentScene.path))
                    {
                        // Scene is untitled, needs a path
                        return new ErrorResponse(
                            "Cannot save an untitled scene without providing a 'name' and 'path'. Use Save As functionality."
                        );
                    }
                    saved = EditorSceneManager.SaveScene(currentScene);
                }

                if (saved)
                {
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                    return new SuccessResponse(
                        $"Scene '{currentScene.name}' saved successfully to '{finalPath}'.",
                        new { path = finalPath, name = currentScene.name }
                    );
                }
                else
                {
                    return new ErrorResponse($"Failed to save scene '{currentScene.name}'.");
                }
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Error saving scene: {e.Message}");
            }
        }

        private static object CaptureScreenshot(SceneCommand cmd, bool previewModeDefault)
        {
            try
            {
                int resolvedSuperSize = (cmd?.superSize.HasValue == true && cmd.superSize.Value > 0) ? cmd.superSize.Value : 1;
                string requestedFileName = string.IsNullOrWhiteSpace(cmd?.fileName) ? null : cmd.fileName;
                ScreenshotRequestOptions request = ResolveScreenshotRequest(cmd, previewModeDefault);

                // Batch mode warning
                if (Application.isBatchMode)
                {
                    McpLog.Warn("[ManageScene] Screenshot capture in batch mode uses camera-based fallback. Results may vary.");
                }

                // Check Screen Capture module availability and camera fallback availability.
                bool screenCaptureAvailable = ScreenshotUtility.IsScreenCaptureModuleAvailable;
                bool hasCameraFallback = ScreenshotUtility.TryFindAvailableCamera(out Camera availableCamera);

#if UNITY_2022_1_OR_NEWER
                if (!screenCaptureAvailable && !hasCameraFallback)
                {
                    return new ErrorResponse(
                        "Cannot capture screenshot. The Screen Capture module is not enabled and no Camera was found in the scene. " +
                        "Please either: (1) Enable the Screen Capture module: Window > Package Manager > Built-in > Screen Capture > Enable, " +
                        "or (2) Add a Camera to your scene for camera-based fallback capture."
                    );
                }

                if (!screenCaptureAvailable)
                {
                    McpLog.Warn("[ManageScene] Screen Capture module not enabled. Using camera-based fallback. " +
                        "For best results, enable it: Window > Package Manager > Built-in > Screen Capture > Enable.");
                }
#else
                if (!hasCameraFallback)
                {
                    return new ErrorResponse(
                        "No camera found in the scene. Screenshot capture on Unity versions before 2022.1 requires a Camera in the scene. " +
                        "Please add a Camera to your scene or upgrade to Unity 2022.1+ for ScreenCapture API support."
                    );
                }
#endif

                if (request.RequiresImmediateCapture && !hasCameraFallback)
                {
                    return new ErrorResponse(
                        "Immediate screenshot mode requires a Camera in the scene. " +
                        "Add a Camera or call action='screenshot' without preview/wait/width/height options."
                    );
                }

                // Best-effort: ensure Game View exists and repaints before capture.
                if (!Application.isBatchMode)
                {
                    EnsureGameView();
                }

                bool useImmediateCapture = request.RequiresImmediateCapture;
                ScreenshotCaptureResult result = useImmediateCapture
                    ? ScreenshotUtility.CaptureFromCameraToAssetsFolder(
                        availableCamera,
                        requestedFileName,
                        resolvedSuperSize,
                        ensureUniqueFileName: true,
                        targetWidth: request.CaptureWidth,
                        targetHeight: request.CaptureHeight)
                    : ScreenshotUtility.CaptureToAssetsFolder(requestedFileName, resolvedSuperSize, ensureUniqueFileName: true);

                bool shouldApplyDownscale = !request.HasCaptureSizeOverride;

                if (result.IsAsync && !useImmediateCapture)
                {
                    double importTimeoutSeconds = cmd?.timeoutMs.HasValue == true
                        ? Mathf.Max(0.25f, request.TimeoutMs / 1000f)
                        : DefaultAsyncScreenshotImportTimeoutSeconds;
                    ScheduleAssetImportWhenFileExists(
                        result.AssetsRelativePath,
                        result.FullPath,
                        timeoutSeconds: importTimeoutSeconds,
                        applyDownscale: shouldApplyDownscale
                    );
                }
                else
                {
                    if (shouldApplyDownscale)
                    {
                        TryDownscaleScreenshotInPlaceIfConfigured(result.FullPath);
                    }
                    AssetDatabase.ImportAsset(result.AssetsRelativePath, ImportAssetOptions.ForceSynchronousImport);
                }

                // Keep the default screenshot call cheap by returning only paths when capture remains async.
                if (result.IsAsync && !useImmediateCapture)
                {
                    string message = $"Screenshot requested to '{result.AssetsRelativePath}' (full: {result.FullPath}).";
                    return new SuccessResponse(
                        message,
                        new
                        {
                            path = result.AssetsRelativePath,
                            fullPath = result.FullPath,
                            superSize = result.SuperSize,
                            isAsync = result.IsAsync,
                            returnMode = request.ReturnMode,
                            waitForWrite = request.WaitForWrite,
                            captureWidth = request.CaptureWidth,
                            captureHeight = request.CaptureHeight,
                        }
                    );
                }

                if (!File.Exists(result.FullPath))
                {
                    return new ErrorResponse($"Screenshot file was not found after capture at '{result.FullPath}'.");
                }

                if (!TryBuildScreenshotPayload(result, request, out object payload, out string payloadError))
                {
                    return new ErrorResponse(payloadError ?? "Failed to build screenshot payload.");
                }

                string successMessage = request.WantsPreview
                    ? $"Screenshot captured to '{result.AssetsRelativePath}' with preview."
                    : $"Screenshot captured to '{result.AssetsRelativePath}'.";

                return new SuccessResponse(successMessage, payload);
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Error capturing screenshot: {e.Message}");
            }
        }

        private static ScreenshotRequestOptions ResolveScreenshotRequest(SceneCommand cmd, bool previewModeDefault)
        {
            var request = new ScreenshotRequestOptions();

            request.ReturnMode = NormalizeReturnMode(cmd?.returnMode, cmd?.returnPreview, previewModeDefault);
            request.WaitForWrite = cmd?.waitForWrite ?? previewModeDefault;
            request.TimeoutMs = Mathf.Clamp(cmd?.timeoutMs ?? 5000, 250, 120000);
            request.PreviewMaxWidth = Mathf.Clamp(cmd?.previewMaxWidth ?? 960, 1, 8192);
            request.PreviewMaxHeight = Mathf.Clamp(cmd?.previewMaxHeight ?? 540, 1, 8192);
            request.PreviewFormat = NormalizePreviewFormat(cmd?.previewFormat);
            request.PreviewJpegQuality = Mathf.Clamp(cmd?.previewJpegQuality ?? 70, 1, 100);
            request.PreviewMaxPixels = Mathf.Clamp(cmd?.previewMaxPixels ?? 600000, 1024, 8000000);
            request.CaptureWidth = ClampCaptureDimension(cmd?.width);
            request.CaptureHeight = ClampCaptureDimension(cmd?.height);

            return request;
        }

        private static int? ClampCaptureDimension(int? value)
        {
            if (!value.HasValue)
            {
                return null;
            }

            int maxDim = Mathf.Max(1, SystemInfo.maxTextureSize);
            return Mathf.Clamp(value.Value, 1, maxDim);
        }

        private static string NormalizeReturnMode(string returnMode, bool? returnPreview, bool previewModeDefault)
        {
            string normalized = (returnMode ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized == "path" || normalized == "preview" || normalized == "both")
            {
                return normalized;
            }

            if (returnPreview.HasValue)
            {
                return returnPreview.Value ? "both" : "path";
            }

            return previewModeDefault ? "both" : "path";
        }

        private static string NormalizePreviewFormat(string previewFormat)
        {
            string format = (previewFormat ?? string.Empty).Trim().ToLowerInvariant();
            if (format == "png")
            {
                return "png";
            }

            if (format == "jpg" || format == "jpeg")
            {
                return "jpg";
            }

            return "jpg";
        }

        private static bool TryBuildScreenshotPayload(ScreenshotCaptureResult result, ScreenshotRequestOptions request, out object payload, out string error)
        {
            payload = null;
            error = null;

            Texture2D source = null;
            try
            {
                if (!TryLoadTextureFromFile(result.FullPath, out source, out string loadError))
                {
                    error = loadError;
                    return false;
                }

                object preview = null;
                if (request.WantsPreview)
                {
                    if (!TryBuildPreviewPayload(source, request, out preview, out string previewError))
                    {
                        error = previewError;
                        return false;
                    }
                }

                long fileSizeBytes = new FileInfo(result.FullPath).Length;
                string sha = ComputeFileSha256Hex(result.FullPath);

                payload = new
                {
                    path = result.AssetsRelativePath,
                    fullPath = result.FullPath,
                    superSize = result.SuperSize,
                    isAsync = result.IsAsync,
                    width = source.width,
                    height = source.height,
                    fileSizeBytes = fileSizeBytes,
                    sha256 = sha,
                    returnMode = request.ReturnMode,
                    waitForWrite = request.WaitForWrite,
                    timeoutMs = request.TimeoutMs,
                    captureWidth = request.CaptureWidth,
                    captureHeight = request.CaptureHeight,
                    preview = preview,
                };
                return true;
            }
            catch (Exception e)
            {
                error = $"Failed to build screenshot payload: {e.Message}";
                return false;
            }
            finally
            {
                if (source != null)
                {
                    if (Application.isPlaying) UnityEngine.Object.Destroy(source);
                    else UnityEngine.Object.DestroyImmediate(source);
                }
            }
        }

        private static bool TryLoadTextureFromFile(string fullPath, out Texture2D texture, out string error)
        {
            texture = null;
            error = null;

            try
            {
                byte[] bytes = File.ReadAllBytes(fullPath);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
                if (!tex.LoadImage(bytes, markNonReadable: false) || tex.width <= 0 || tex.height <= 0)
                {
                    if (Application.isPlaying) UnityEngine.Object.Destroy(tex);
                    else UnityEngine.Object.DestroyImmediate(tex);
                    error = $"Failed to decode screenshot image at '{fullPath}'.";
                    return false;
                }

                texture = tex;
                return true;
            }
            catch (Exception e)
            {
                error = $"Failed to read screenshot file '{fullPath}': {e.Message}";
                return false;
            }
        }

        private static bool TryBuildPreviewPayload(Texture2D source, ScreenshotRequestOptions request, out object previewPayload, out string error)
        {
            previewPayload = null;
            error = null;

            RenderTexture rt = null;
            RenderTexture prevActive = null;
            Texture2D previewTexture = null;
            bool restorePrevActive = false;
            try
            {
                Vector2Int targetSize = ComputePreviewDimensions(source.width, source.height, request);

                if (targetSize.x == source.width && targetSize.y == source.height)
                {
                    previewTexture = source;
                }
                else
                {
                    prevActive = RenderTexture.active;
                    restorePrevActive = true;

                    rt = RenderTexture.GetTemporary(targetSize.x, targetSize.y, 0, RenderTextureFormat.ARGB32);
                    if (rt == null)
                    {
                        error = "Failed to allocate preview render texture.";
                        return false;
                    }
                    rt.filterMode = FilterMode.Bilinear;

                    Graphics.Blit(source, rt);
                    RenderTexture.active = rt;

                    previewTexture = new Texture2D(targetSize.x, targetSize.y, TextureFormat.RGB24, mipChain: false);
                    previewTexture.ReadPixels(new Rect(0, 0, targetSize.x, targetSize.y), 0, 0);
                    previewTexture.Apply();
                }

                bool writePng = string.Equals(request.PreviewFormat, "png", StringComparison.OrdinalIgnoreCase);
                byte[] encoded = writePng
                    ? previewTexture.EncodeToPNG()
                    : previewTexture.EncodeToJPG(request.PreviewJpegQuality);

                if (encoded == null || encoded.Length == 0)
                {
                    error = "Failed to encode screenshot preview.";
                    return false;
                }

                previewPayload = new
                {
                    mimeType = writePng ? "image/png" : "image/jpeg",
                    width = previewTexture.width,
                    height = previewTexture.height,
                    blob = Convert.ToBase64String(encoded),
                };
                return true;
            }
            catch (Exception e)
            {
                error = $"Failed to build screenshot preview: {e.Message}";
                return false;
            }
            finally
            {
                if (restorePrevActive)
                {
                    RenderTexture.active = prevActive;
                }

                if (rt != null)
                {
                    RenderTexture.ReleaseTemporary(rt);
                }

                if (previewTexture != null && !ReferenceEquals(previewTexture, source))
                {
                    if (Application.isPlaying) UnityEngine.Object.Destroy(previewTexture);
                    else UnityEngine.Object.DestroyImmediate(previewTexture);
                }
            }
        }

        private static Vector2Int ComputePreviewDimensions(int sourceWidth, int sourceHeight, ScreenshotRequestOptions request)
        {
            if (sourceWidth <= 0 || sourceHeight <= 0)
            {
                return new Vector2Int(1, 1);
            }

            float scaleByWidth = request.PreviewMaxWidth > 0 ? (float)request.PreviewMaxWidth / sourceWidth : 1f;
            float scaleByHeight = request.PreviewMaxHeight > 0 ? (float)request.PreviewMaxHeight / sourceHeight : 1f;
            float sourcePixels = Mathf.Max(1f, sourceWidth * sourceHeight);
            float scaleByPixels = request.PreviewMaxPixels > 0
                ? Mathf.Sqrt(request.PreviewMaxPixels / sourcePixels)
                : 1f;

            float scale = Mathf.Min(1f, scaleByWidth, scaleByHeight, scaleByPixels);
            int targetWidth = Mathf.Max(1, Mathf.RoundToInt(sourceWidth * scale));
            int targetHeight = Mathf.Max(1, Mathf.RoundToInt(sourceHeight * scale));
            return new Vector2Int(targetWidth, targetHeight);
        }

        private static string ComputeFileSha256Hex(string fullPath)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(fullPath))
            {
                byte[] hash = sha.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static void EnsureGameView()
        {
            try
            {
                // Ensure a Game View exists and has a chance to repaint before capture.
                try
                {
                    if (!EditorApplication.ExecuteMenuItem("Window/General/Game"))
                    {
                        // Some Unity versions expose hotkey suffixes in menu paths.
                        EditorApplication.ExecuteMenuItem("Window/General/Game %2");
                    }
                }
                catch (Exception e)
                {
                    try { McpLog.Debug($"[ManageScene] screenshot: failed to open Game View via menu item: {e.Message}"); } catch { }
                }

                try
                {
                    var gameViewType = Type.GetType("UnityEditor.GameView,UnityEditor");
                    if (gameViewType != null)
                    {
                        var window = EditorWindow.GetWindow(gameViewType);
                        window?.Repaint();
                    }
                }
                catch (Exception e)
                {
                    try { McpLog.Debug($"[ManageScene] screenshot: failed to repaint Game View: {e.Message}"); } catch { }
                }

                try { SceneView.RepaintAll(); }
                catch (Exception e)
                {
                    try { McpLog.Debug($"[ManageScene] screenshot: failed to repaint Scene View: {e.Message}"); } catch { }
                }

                try { EditorApplication.QueuePlayerLoopUpdate(); }
                catch (Exception e)
                {
                    try { McpLog.Debug($"[ManageScene] screenshot: failed to queue player loop update: {e.Message}"); } catch { }
                }
            }
            catch (Exception e)
            {
                try { McpLog.Debug($"[ManageScene] screenshot: EnsureGameView failed: {e.Message}"); } catch { }
            }
        }

        private static void ScheduleAssetImportWhenFileExists(string assetsRelativePath, string fullPath, double timeoutSeconds, bool applyDownscale = true)
        {
            if (string.IsNullOrWhiteSpace(assetsRelativePath) || string.IsNullOrWhiteSpace(fullPath))
            {
                McpLog.Warn("[ManageScene] ScheduleAssetImportWhenFileExists: invalid paths provided, skipping import scheduling.");
                return;
            }

            double start = EditorApplication.timeSinceStartup;
            int failureCount = 0;
            bool hasSeenFile = false;
            bool readyToImport = false;
            const int maxLoggedFailures = 3;
            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                try
                {
                    if (File.Exists(fullPath))
                    {
                        hasSeenFile = true;

                        if (!readyToImport)
                        {
                            // Optional downscale before import, so the imported asset matches what we want on disk.
                            // If downscale is disabled for this capture, import as soon as the file exists.
                            // If the screenshot file is still being written/locked, wait for the next tick.
                            readyToImport = applyDownscale
                                ? TryDownscaleScreenshotInPlaceIfConfigured(fullPath)
                                : true;
                            // If not ready, don't return early; allow timeout/unsubscribe logic to run below.
                        }

                        if (readyToImport)
                        {
                            AssetDatabase.ImportAsset(assetsRelativePath, ImportAssetOptions.ForceSynchronousImport);
                            McpLog.Debug($"[ManageScene] Imported asset at '{assetsRelativePath}'.");
                            EditorApplication.update -= tick;
                            return;
                        }
                    }
                }
                catch (Exception e)
                {
                    failureCount++;

                    if (failureCount <= maxLoggedFailures)
                    {
                        McpLog.Warn($"[ManageScene] Exception while importing asset '{assetsRelativePath}' from '{fullPath}' (attempt {failureCount}): {e}");
                    }
                }

                if (EditorApplication.timeSinceStartup - start > timeoutSeconds)
                {
                    if (!hasSeenFile)
                    {
                        McpLog.Warn($"[ManageScene] Timed out waiting for file '{fullPath}' (asset: '{assetsRelativePath}') after {timeoutSeconds:F1} seconds. The asset was not imported.");
                    }
                    else
                    {
                        McpLog.Warn($"[ManageScene] Timed out importing asset '{assetsRelativePath}' from '{fullPath}' after {timeoutSeconds:F1} seconds. The file existed but the asset was not imported.");
                    }

                    EditorApplication.update -= tick;
                }
            };

            EditorApplication.update += tick;
        }

        private static float GetScreenshotDownscaleFactor()
        {
            try
            {
                float factor = EditorPrefs.GetFloat(EditorPrefKeys.ScreenshotDownscaleFactor, 1f);
                if (float.IsNaN(factor) || float.IsInfinity(factor)) return 1f;
                return Mathf.Clamp(factor, 0.1f, 1f);
            }
            catch
            {
                return 1f;
            }
        }

        /// <summary>
        /// Returns true if it's safe to proceed with import. Returns false when the file appears locked/incomplete and we should retry.
        /// </summary>
        private static bool TryDownscaleScreenshotInPlaceIfConfigured(string fullPath)
        {
            float factor = GetScreenshotDownscaleFactor();
            if (factor >= 0.999f) return true;

            try
            {
                if (!File.Exists(fullPath)) return false;

                byte[] bytes;
                try
                {
                    bytes = File.ReadAllBytes(fullPath);
                }
                catch (IOException)
                {
                    return false;
                }

                var src = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
                Texture2D dst = null;
                RenderTexture rt = null;
                RenderTexture prevActive = null;
                bool restorePrevActive = false;

                try
                {
                    prevActive = RenderTexture.active;
                    restorePrevActive = true;

                    bool loaded = src.LoadImage(bytes, markNonReadable: false);
                    if (!loaded || src.width <= 0 || src.height <= 0)
                    {
                        return true; // can't decode; proceed with original
                    }

                    int targetWidth = Mathf.Max(1, Mathf.RoundToInt(src.width * factor));
                    int targetHeight = Mathf.Max(1, Mathf.RoundToInt(src.height * factor));
                    if (targetWidth == src.width && targetHeight == src.height)
                    {
                        return true;
                    }

                    rt = RenderTexture.GetTemporary(targetWidth, targetHeight, 0, RenderTextureFormat.ARGB32);
                    if (rt == null)
                    {
                        return true; // can't allocate; proceed with original
                    }
                    rt.filterMode = FilterMode.Bilinear;

                    Graphics.Blit(src, rt);

                    RenderTexture.active = rt;
                    dst = new Texture2D(targetWidth, targetHeight, TextureFormat.RGBA32, mipChain: false);
                    dst.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
                    dst.Apply();

                    string ext = Path.GetExtension(fullPath) ?? string.Empty;
                    bool writeJpeg =
                        ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);

                    byte[] encoded = writeJpeg ? dst.EncodeToJPG(90) : dst.EncodeToPNG();
                    try
                    {
                        File.WriteAllBytes(fullPath, encoded);
                    }
                    catch (IOException)
                    {
                        return false;
                    }

                    return true;
                }
                finally
                {
                    if (restorePrevActive)
                    {
                        RenderTexture.active = prevActive;
                    }

                    if (rt != null)
                    {
                        RenderTexture.ReleaseTemporary(rt);
                    }

                    if (src != null)
                    {
                        if (Application.isPlaying) UnityEngine.Object.Destroy(src);
                        else UnityEngine.Object.DestroyImmediate(src);
                    }

                    if (dst != null)
                    {
                        if (Application.isPlaying) UnityEngine.Object.Destroy(dst);
                        else UnityEngine.Object.DestroyImmediate(dst);
                    }
                }
            }
            catch (Exception e)
            {
                McpLog.Debug($"[ManageScene] screenshot: downscale failed for '{fullPath}': {e.Message}");
                return true; // don't block import on unexpected errors
            }
        }

        private static object GetActiveSceneInfo()
        {
            try
            {
                try { McpLog.Info("[ManageScene] get_active: querying EditorSceneManager.GetActiveScene", always: false); } catch { }
                Scene activeScene = EditorSceneManager.GetActiveScene();
                try { McpLog.Info($"[ManageScene] get_active: got scene valid={activeScene.IsValid()} loaded={activeScene.isLoaded} name='{activeScene.name}'", always: false); } catch { }
                if (!activeScene.IsValid())
                {
                    return new ErrorResponse("No active scene found.");
                }

                var sceneInfo = new
                {
                    name = activeScene.name,
                    path = activeScene.path,
                    buildIndex = activeScene.buildIndex, // -1 if not in build settings
                    isDirty = activeScene.isDirty,
                    isLoaded = activeScene.isLoaded,
                    rootCount = activeScene.rootCount,
                };

                return new SuccessResponse("Retrieved active scene information.", sceneInfo);
            }
            catch (Exception e)
            {
                try { McpLog.Error($"[ManageScene] get_active: exception {e.Message}"); } catch { }
                return new ErrorResponse($"Error getting active scene info: {e.Message}");
            }
        }

        private static object GetBuildSettingsScenes()
        {
            try
            {
                var scenes = new List<object>();
                for (int i = 0; i < EditorBuildSettings.scenes.Length; i++)
                {
                    var scene = EditorBuildSettings.scenes[i];
                    scenes.Add(
                        new
                        {
                            path = scene.path,
                            guid = scene.guid.ToString(),
                            enabled = scene.enabled,
                            buildIndex = i, // Actual build index considering only enabled scenes might differ
                        }
                    );
                }
                return new SuccessResponse("Retrieved scenes from Build Settings.", scenes);
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Error getting scenes from Build Settings: {e.Message}");
            }
        }

        private static object GetSceneHierarchyPaged(SceneCommand cmd)
        {
            try
            {
                // Check Prefab Stage first
                var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
                Scene activeScene;
                
                if (prefabStage != null)
                {
                    activeScene = prefabStage.scene;
                    try { McpLog.Info("[ManageScene] get_hierarchy: using Prefab Stage scene", always: false); } catch { }
                }
                else
                {
                    try { McpLog.Info("[ManageScene] get_hierarchy: querying EditorSceneManager.GetActiveScene", always: false); } catch { }
                    activeScene = EditorSceneManager.GetActiveScene();
                }
                
                try { McpLog.Info($"[ManageScene] get_hierarchy: got scene valid={activeScene.IsValid()} loaded={activeScene.isLoaded} name='{activeScene.name}'", always: false); } catch { }
                if (!activeScene.IsValid() || !activeScene.isLoaded)
                {
                    return new ErrorResponse(
                        "No valid and loaded scene is active to get hierarchy from."
                    );
                }

                // Defaults tuned for safety; callers can override but we clamp to sane maxes.
                // NOTE: pageSize is "items per page", not "number of pages".
                // Keep this conservative to reduce peak response sizes when callers omit page_size.
                int resolvedPageSize = Mathf.Clamp(cmd.pageSize ?? 50, 1, 500);
                int resolvedCursor = Mathf.Max(0, cmd.cursor ?? 0);
                int resolvedMaxNodes = Mathf.Clamp(cmd.maxNodes ?? 1000, 1, 5000);
                int effectiveTake = Mathf.Min(resolvedPageSize, resolvedMaxNodes);
                int resolvedMaxChildrenPerNode = Mathf.Clamp(cmd.maxChildrenPerNode ?? 200, 0, 2000);
                bool includeTransform = cmd.includeTransform ?? false;

                // NOTE: maxDepth is accepted for forward-compatibility, but current paging mode
                // returns a single level (roots or direct children). This keeps payloads bounded.

                List<GameObject> nodes;
                string scope;

                GameObject parentGo = ResolveGameObject(cmd.parent, activeScene);
                if (cmd.parent == null || cmd.parent.Type == JTokenType.Null)
                {
                    try { McpLog.Info("[ManageScene] get_hierarchy: listing root objects (paged summary)", always: false); } catch { }
                    nodes = activeScene.GetRootGameObjects().Where(go => go != null).ToList();
                    scope = "roots";
                }
                else
                {
                    if (parentGo == null)
                    {
                        return new ErrorResponse($"Parent GameObject ('{cmd.parent}') not found.");
                    }
                    try { McpLog.Info($"[ManageScene] get_hierarchy: listing children of '{parentGo.name}' (paged summary)", always: false); } catch { }
                    nodes = new List<GameObject>(parentGo.transform.childCount);
                    foreach (Transform child in parentGo.transform)
                    {
                        if (child != null) nodes.Add(child.gameObject);
                    }
                    scope = "children";
                }

                int total = nodes.Count;
                if (resolvedCursor > total) resolvedCursor = total;
                int end = Mathf.Min(total, resolvedCursor + effectiveTake);

                var items = new List<object>(Mathf.Max(0, end - resolvedCursor));
                for (int i = resolvedCursor; i < end; i++)
                {
                    var go = nodes[i];
                    if (go == null) continue;
                    items.Add(BuildGameObjectSummary(go, includeTransform, resolvedMaxChildrenPerNode));
                }

                bool truncated = end < total;
                string nextCursor = truncated ? end.ToString() : null;

                var payload = new
                {
                    scope = scope,
                    cursor = resolvedCursor,
                    pageSize = effectiveTake,
                    next_cursor = nextCursor,
                    truncated = truncated,
                    total = total,
                    items = items,
                };

                var resp = new SuccessResponse($"Retrieved hierarchy page for scene '{activeScene.name}'.", payload);
                try { McpLog.Info("[ManageScene] get_hierarchy: success", always: false); } catch { }
                return resp;
            }
            catch (Exception e)
            {
                try { McpLog.Error($"[ManageScene] get_hierarchy: exception {e.Message}"); } catch { }
                return new ErrorResponse($"Error getting scene hierarchy: {e.Message}");
            }
        }

        private static GameObject ResolveGameObject(JToken targetToken, Scene activeScene)
        {
            if (targetToken == null || targetToken.Type == JTokenType.Null) return null;

            try
            {
                if (targetToken.Type == JTokenType.Integer || int.TryParse(targetToken.ToString(), out _))
                {
                    if (int.TryParse(targetToken.ToString(), out int id))
                    {
                        var obj = EditorUtility.InstanceIDToObject(id);
                        if (obj is GameObject go) return go;
                        if (obj is Component c) return c.gameObject;
                    }
                }
            }
            catch { }

            string s = targetToken.ToString();
            if (string.IsNullOrEmpty(s)) return null;

            // Path-based find (e.g., "Root/Child/GrandChild")
            if (s.Contains("/"))
            {
                try
                {
                    var ids = GameObjectLookup.SearchGameObjects("by_path", s, includeInactive: true, maxResults: 1);
                    if (ids.Count > 0)
                    {
                        var byPath = GameObjectLookup.FindById(ids[0]);
                        if (byPath != null) return byPath;
                    }
                }
                catch { }
            }

            // Name-based find (first match, includes inactive)
            try
            {
                var all = activeScene.GetRootGameObjects();
                foreach (var root in all)
                {
                    if (root == null) continue;
                    if (root.name == s) return root;
                    var trs = root.GetComponentsInChildren<Transform>(includeInactive: true);
                    foreach (var t in trs)
                    {
                        if (t != null && t.gameObject != null && t.gameObject.name == s) return t.gameObject;
                    }
                }
            }
            catch { }

            return null;
        }

        private static object BuildGameObjectSummary(GameObject go, bool includeTransform, int maxChildrenPerNode)
        {
            if (go == null) return null;

            int childCount = 0;
            try { childCount = go.transform != null ? go.transform.childCount : 0; } catch { }
            bool childrenTruncated = childCount > 0; // We do not inline children in summary mode.

            // Get component type names (lightweight - no full serialization)
            var componentTypes = new List<string>();
            try
            {
                var components = go.GetComponents<Component>();
                if (components != null)
                {
                    foreach (var c in components)
                    {
                        if (c != null)
                        {
                            componentTypes.Add(c.GetType().Name);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                McpLog.Debug($"[ManageScene] Failed to enumerate components for '{go.name}': {ex.Message}");
            }

            var d = new Dictionary<string, object>
            {
                { "name", go.name },
                { "instanceID", go.GetInstanceID() },
                { "activeSelf", go.activeSelf },
                { "activeInHierarchy", go.activeInHierarchy },
                { "tag", go.tag },
                { "layer", go.layer },
                { "isStatic", go.isStatic },
                { "path", GetGameObjectPath(go) },
                { "childCount", childCount },
                { "childrenTruncated", childrenTruncated },
                { "childrenCursor", childCount > 0 ? "0" : null },
                { "childrenPageSizeDefault", maxChildrenPerNode },
                { "componentTypes", componentTypes },
            };

            if (includeTransform && go.transform != null)
            {
                var t = go.transform;
                d["transform"] = new
                {
                    position = new[] { t.localPosition.x, t.localPosition.y, t.localPosition.z },
                    rotation = new[] { t.localRotation.eulerAngles.x, t.localRotation.eulerAngles.y, t.localRotation.eulerAngles.z },
                    scale = new[] { t.localScale.x, t.localScale.y, t.localScale.z },
                };
            }

            return d;
        }

        private static string GetGameObjectPath(GameObject go)
        {
            if (go == null) return string.Empty;
            try
            {
                var names = new Stack<string>();
                Transform t = go.transform;
                while (t != null)
                {
                    names.Push(t.name);
                    t = t.parent;
                }
                return string.Join("/", names);
            }
            catch
            {
                return go.name;
            }
        }

    }
}
