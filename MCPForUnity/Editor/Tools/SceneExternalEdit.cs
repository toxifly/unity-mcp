using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Rewrites a scene asset on disk and brings the Editor back in sync as one main-thread step.
    /// </summary>
    /// <remarks>
    /// Hand-editing a .unity file while that scene is open makes Unity notice the change on its
    /// next refresh and raise a modal "reload scene?" prompt. That dialog runs a nested message
    /// loop on the Editor's main thread, which is the thread the MCP bridge pumps on, so no command
    /// this side sends can ever answer it — the bridge just stops responding until a human clicks.
    /// The fix is to never let the situation arise: close the scene, write, reimport, reopen, all
    /// before returning control to the Editor, so there is no window in which the open scene and its
    /// file disagree.
    /// </remarks>
    public static class SceneExternalEdit
    {
        internal static Action<string, string, string> ReplaceFileOverrideForTests { get; set; }
        internal static Action<string, string> MoveFileOverrideForTests { get; set; }
        internal static Func<string, FileAttributes> GetFileAttributesOverrideForTests { get; set; }

        public static object Apply(JObject @params)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return new ErrorResponse(
                    "Cannot rewrite a scene asset during Play Mode. Stop Play Mode (manage_editor action=stop) and retry.");
            }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return new ErrorResponse("Editor is compiling or importing; retry once it is idle.");
            }

            var p = new ToolParams(@params);
            if (!TryNormalizeScenePath(
                    p.Get("path") ?? p.Get("scene_path"),
                    out string assetPath,
                    out string pathError))
            {
                return new ErrorResponse(pathError);
            }

            string fullPath = Path.Combine(ProjectRoot(), assetPath);
            if (!File.Exists(fullPath))
            {
                return new ErrorResponse($"Scene file not found at '{assetPath}'.");
            }

            byte[] original = File.ReadAllBytes(fullPath);
            bool hasBom = original.Length >= 3
                && original[0] == 0xEF && original[1] == 0xBB && original[2] == 0xBF;
            var encoding = new UTF8Encoding(false);
            int offset = hasBom ? 3 : 0;
            string text = encoding.GetString(original, offset, original.Length - offset);

            var applied = new List<object>();
            if (!TryApplyEdits(@params["edits"], ref text, applied, out string editError))
            {
                return new ErrorResponse(editError + " Nothing was written.");
            }

            bool dryRun = p.GetBool("dry_run");
            byte[] rewritten = Combine(hasBom, encoding.GetBytes(text));

            if (dryRun)
            {
                return new SuccessResponse(
                    $"Dry run: {applied.Count} edit(s) matched '{assetPath}'. Nothing was written.",
                    new
                    {
                        path = assetPath,
                        dryRun = true,
                        edits = applied,
                        bytesBefore = original.Length,
                        bytesAfter = rewritten.Length,
                    });
            }

            return WriteAndReload(assetPath, fullPath, original, rewritten, applied, p);
        }

        /// <summary>
        /// Closes the scene before replacing its asset, so Unity's external-change detection never
        /// sees an open scene whose file has moved underneath it.
        /// </summary>
        private static object WriteAndReload(
            string assetPath,
            string fullPath,
            byte[] original,
            byte[] rewritten,
            List<object> applied,
            ToolParams p)
        {
            Scene target = default;
            bool targetIsOpen = false;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                if (string.Equals(NormalizeSeparators(scene.path), assetPath, StringComparison.OrdinalIgnoreCase))
                {
                    target = scene;
                    targetIsOpen = true;
                }
            }

            if (targetIsOpen && SceneManager.sceneCount > 1)
            {
                return new ErrorResponse(
                    $"'{assetPath}' is open alongside {SceneManager.sceneCount - 1} other scene(s) in the hierarchy. "
                    + "This rewrite closes every scene to write safely and only restores the target, so close the "
                    + "additive scenes first and remove them from the hierarchy "
                    + "(manage_scene action=close_scene remove_scene=true).");
            }

            bool discardUnsaved = p.GetBool("discard_unsaved");
            if (targetIsOpen && target.isDirty && !discardUnsaved)
            {
                return new ErrorResponse(
                    $"'{assetPath}' has unsaved in-memory changes that this rewrite would discard. "
                    + "Save them first (manage_scene action=save), or pass discard_unsaved=true to drop them.");
            }

            // Re-check immediately before staging the replacement. The path was checked before it
            // was read, but an ancestor could have been replaced with a link while edits were being
            // prepared. This keeps both the read and write sides of the transaction inside Assets.
            if (!TryRejectReparsePoints(fullPath, out string pathError))
            {
                return new ErrorResponse(pathError);
            }

            string transactionPath = fullPath + ".mcp-" + Guid.NewGuid().ToString("N");
            string tempPath = transactionPath + ".tmp";
            string backupPath = transactionPath + ".bak";
            string displacedPath = transactionPath + ".failed";
            bool replacementCommitted = false;
            bool preserveBackup = false;
            bool targetWasClosed = false;
            AssetDatabase.DisallowAutoRefresh();
            try
            {
                // Stage the rewrite before closing the target to keep the Editor on the empty scene
                // only for the brief replace/import/reopen portion of the transaction.
                File.WriteAllBytes(tempPath, rewritten);

                if (targetIsOpen)
                {
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                    targetWasClosed = true;
                }

                ReplaceWithBackup(tempPath, fullPath, backupPath, displacedPath);
                replacementCommitted = true;
                AssetDatabase.ImportAsset(
                    assetPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

                if (targetIsOpen)
                {
                    EditorSceneManager.OpenScene(assetPath, OpenSceneMode.Single);
                }
            }
            catch (Exception e)
            {
                var recoveryErrors = new List<string>();
                bool originalOnDisk = !replacementCommitted;

                // Keep the backup until import and scene reopening have both succeeded. If either
                // operation fails after the replace, roll the bytes back before returning control to
                // Unity so the Editor never remains attached to the rewritten or untitled scene.
                if (replacementCommitted || File.Exists(backupPath))
                {
                    try
                    {
                        RestoreBackup(fullPath, backupPath, displacedPath);
                        originalOnDisk = true;
                    }
                    catch (Exception restoreError)
                    {
                        originalOnDisk = false;
                        preserveBackup = File.Exists(backupPath);
                        recoveryErrors.Add($"could not restore the original file: {restoreError.Message}");
                    }
                }

                if (originalOnDisk && replacementCommitted)
                {
                    try
                    {
                        AssetDatabase.ImportAsset(
                            assetPath,
                            ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                    }
                    catch (Exception importError)
                    {
                        recoveryErrors.Add($"could not reimport the restored file: {importError.Message}");
                    }
                }

                if (targetWasClosed && originalOnDisk)
                {
                    try
                    {
                        EditorSceneManager.OpenScene(assetPath, OpenSceneMode.Single);
                    }
                    catch (Exception reopenError)
                    {
                        recoveryErrors.Add($"could not reopen the original scene: {reopenError.Message}");
                    }
                }

                string recovery = recoveryErrors.Count == 0
                    ? (targetWasClosed
                        ? " The original file was restored and the scene was reopened."
                        : " The original file was restored.")
                    : " Recovery was incomplete: " + string.Join("; ", recoveryErrors) + ".";
                if (preserveBackup)
                {
                    recovery += $" The original bytes remain at '{backupPath}'.";
                }

                return new ErrorResponse($"Error rewriting scene '{assetPath}': {e.Message}.{recovery}");
            }
            finally
            {
                TryDelete(tempPath);
                TryDelete(displacedPath);
                if (!preserveBackup) TryDelete(backupPath);
                AssetDatabase.AllowAutoRefresh();
            }

            return new SuccessResponse(
                $"Scene '{assetPath}' rewritten ({applied.Count} edit(s))"
                + (targetIsOpen ? " and reloaded." : "; it was not open, so nothing was reloaded."),
                new
                {
                    path = assetPath,
                    edits = applied,
                    bytesBefore = original.Length,
                    bytesAfter = rewritten.Length,
                    reopened = targetIsOpen,
                });
        }

        /// <summary>
        /// Uses File.Replace when the filesystem supports it. The fallback uses two same-directory
        /// renames, retaining the original at <paramref name="backupPath"/> until the caller has
        /// successfully reimported and (when needed) reopened the scene.
        /// </summary>
        private static void ReplaceWithBackup(
            string tempPath,
            string fullPath,
            string backupPath,
            string displacedPath)
        {
            try
            {
                ReplaceFile(tempPath, fullPath, backupPath);
                return;
            }
            catch (Exception e) when (e is PlatformNotSupportedException || e is IOException)
            {
                // File.Replace is unavailable on some Unity-supported filesystems. Its documented
                // failure leaves the source and destination intact, so fall through to renames that
                // keep the old destination recoverable throughout the operation.
            }

            bool originalMoved = false;
            try
            {
                MoveFile(fullPath, backupPath);
                originalMoved = true;
                MoveFile(tempPath, fullPath);
            }
            catch
            {
                if (originalMoved && File.Exists(backupPath))
                {
                    RestoreBackup(fullPath, backupPath, displacedPath);
                }
                throw;
            }
        }

        private static void RestoreBackup(string fullPath, string backupPath, string displacedPath)
        {
            if (!File.Exists(backupPath))
            {
                throw new FileNotFoundException("The scene transaction backup is missing.", backupPath);
            }

            bool currentDisplaced = false;
            if (File.Exists(fullPath))
            {
                TryDelete(displacedPath);
                MoveFile(fullPath, displacedPath);
                currentDisplaced = true;
            }

            try
            {
                MoveFile(backupPath, fullPath);
            }
            catch
            {
                if (currentDisplaced && !File.Exists(fullPath) && File.Exists(displacedPath))
                {
                    try { MoveFile(displacedPath, fullPath); } catch { }
                }
                throw;
            }

            if (currentDisplaced) TryDelete(displacedPath);
        }

        private static void ReplaceFile(string sourcePath, string destinationPath, string backupPath)
        {
            if (ReplaceFileOverrideForTests != null)
            {
                ReplaceFileOverrideForTests(sourcePath, destinationPath, backupPath);
                return;
            }
            File.Replace(sourcePath, destinationPath, backupPath);
        }

        private static void MoveFile(string sourcePath, string destinationPath)
        {
            if (MoveFileOverrideForTests != null)
            {
                MoveFileOverrideForTests(sourcePath, destinationPath);
                return;
            }
            File.Move(sourcePath, destinationPath);
        }

        private static void TryDelete(string path)
        {
            if (!File.Exists(path)) return;
            try { File.Delete(path); } catch { }
        }

        /// <summary>
        /// Literal anchored replacements. Every anchor must occur exactly as many times as the caller
        /// says, so a moved anchor fails the whole call instead of writing a partial edit.
        /// </summary>
        private static bool TryApplyEdits(JToken editsToken, ref string text, List<object> applied, out string error)
        {
            error = null;
            if (editsToken == null || editsToken.Type == JTokenType.Null)
            {
                // No edits is a legitimate call: a pure discard-and-reload of a file already changed
                // on disk, which is the rescue path for an edit made outside this tool.
                return true;
            }

            if (!(editsToken is JArray edits))
            {
                error = "'edits' must be an array of {old_text, new_text} objects.";
                return false;
            }

            for (int i = 0; i < edits.Count; i++)
            {
                JToken edit = edits[i];
                string oldText = (edit["old_text"] ?? edit["oldText"])?.ToString();
                string newText = (edit["new_text"] ?? edit["newText"])?.ToString() ?? string.Empty;
                if (string.IsNullOrEmpty(oldText))
                {
                    error = $"Edit {i} has no 'old_text' anchor.";
                    return false;
                }

                int expected = ParamCoercion.CoerceIntNullable(edit["count"]) ?? 1;
                int actual = CountOccurrences(text, oldText);
                if (actual != expected)
                {
                    error = $"Edit {i} expected {expected} occurrence(s) of its anchor but found {actual}.";
                    return false;
                }

                text = text.Replace(oldText, newText);
                applied.Add(new { index = i, occurrences = actual });
            }

            return true;
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0;
            int at = 0;
            while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
            {
                count++;
                at += needle.Length;
            }
            return count;
        }

        private static byte[] Combine(bool hasBom, byte[] body)
        {
            if (!hasBom) return body;
            var withBom = new byte[body.Length + 3];
            withBom[0] = 0xEF;
            withBom[1] = 0xBB;
            withBom[2] = 0xBF;
            Buffer.BlockCopy(body, 0, withBom, 3, body.Length);
            return withBom;
        }

        private static bool TryNormalizeScenePath(string path, out string assetPath, out string error)
        {
            assetPath = null;
            error = null;
            if (string.IsNullOrWhiteSpace(path))
            {
                error = "'path' is required and must name a .unity scene asset.";
                return false;
            }

            string normalized = NormalizeSeparators(path.Trim());
            if (!normalized.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                error = "'path' must name a .unity scene asset.";
                return false;
            }

            try
            {
                // Unity asset paths are project-relative. Reject rooted paths before combining them
                // so Path.Combine cannot discard the intended Assets root (including drive and UNC
                // paths). The drive check also catches Windows paths when tests run on another OS.
                if (Path.IsPathRooted(normalized)
                    || normalized.StartsWith("/", StringComparison.Ordinal)
                    || (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':'))
                {
                    error = "'path' must be a Unity asset path relative to the project's Assets "
                        + "directory, not an absolute path.";
                    return false;
                }

                string[] segments = normalized.Split('/');
                foreach (string segment in segments)
                {
                    if (string.Equals(segment, "..", StringComparison.Ordinal))
                    {
                        error = "'path' cannot contain '..' traversal segments and must remain "
                            + "inside the project's Assets directory.";
                        return false;
                    }
                }

                string relativePath = normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                    ? normalized.Substring("Assets/".Length)
                    : normalized;

                string assetsRoot = Path.GetFullPath(UnityEngine.Application.dataPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string assetsPrefix = assetsRoot + Path.DirectorySeparatorChar;
                string fullPath = Path.GetFullPath(Path.Combine(assetsRoot, relativePath));
                StringComparison pathComparison = Path.DirectorySeparatorChar == '\\'
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;

                if (!fullPath.StartsWith(assetsPrefix, pathComparison))
                {
                    error = "'path' must resolve inside the project's Assets directory.";
                    return false;
                }

                if (!TryRejectReparsePoints(fullPath, out error))
                {
                    return false;
                }

                assetPath = "Assets/" + NormalizeSeparators(fullPath.Substring(assetsPrefix.Length));
                return true;
            }
            catch (Exception e) when (
                e is ArgumentException
                || e is IOException
                || e is NotSupportedException
                || e is UnauthorizedAccessException
                || e is System.Security.SecurityException)
            {
                error = $"'path' is not a valid Unity asset path: {e.Message}";
                return false;
            }
        }

        private static bool TryRejectReparsePoints(string fullPath, out string error)
        {
            error = null;
            string assetsRoot = Path.GetFullPath(UnityEngine.Application.dataPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            StringComparison pathComparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            string current = Path.GetFullPath(fullPath);
            while (!string.IsNullOrEmpty(current))
            {
                if (File.Exists(current) || Directory.Exists(current))
                {
                    FileAttributes attributes = GetFileAttributesOverrideForTests != null
                        ? GetFileAttributesOverrideForTests(current)
                        : File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        error = "'path' cannot use a symbolic link or junction; scene edits must "
                            + "stay on physical paths inside the project's Assets directory.";
                        return false;
                    }
                }

                if (string.Equals(current, assetsRoot, pathComparison))
                {
                    break;
                }

                current = Directory.GetParent(current)?.FullName;
            }

            return true;
        }

        private static string NormalizeSeparators(string path) =>
            string.IsNullOrEmpty(path) ? path : path.Replace('\\', '/');

        private static string ProjectRoot() =>
            UnityEngine.Application.dataPath.Substring(
                0, UnityEngine.Application.dataPath.Length - "Assets".Length);
    }
}
