using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.Server;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace MCPForUnity.Editor.Helpers
{
    /// <summary>
    /// Provides common utility methods for working with Unity asset paths.
    /// </summary>
    public static class AssetPathUtility
    {
        /// <summary>
        /// Normalizes path separators to forward slashes without modifying the path structure.
        /// Use this for non-asset paths (e.g., file system paths, relative directories).
        /// </summary>
        public static string NormalizeSeparators(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;
            return path.Replace('\\', '/');
        }

        /// <summary>
        /// Normalizes a Unity asset path by ensuring forward slashes are used and that it is rooted under "Assets/".
        /// Also protects against path traversal attacks using "../" sequences.
        /// </summary>
        public static string SanitizeAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            path = NormalizeSeparators(path);

            // Check for path traversal sequences
            if (path.Contains(".."))
            {
                McpLog.Warn($"[AssetPathUtility] Path contains potential traversal sequence: '{path}'");
                return null;
            }

            // Ensure path starts with Assets/
            if (string.Equals(path, "Assets", StringComparison.OrdinalIgnoreCase))
            {
                return "Assets";
            }
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                return "Assets/" + path.TrimStart('/');
            }

            return path;
        }

        /// <summary>
        /// Checks if a given asset path is valid and safe (no traversal, within Assets folder).
        /// </summary>
        /// <returns>True if the path is valid, false otherwise.</returns>
        public static bool IsValidAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            // Normalize for comparison
            string normalized = NormalizeSeparators(path);

            // Must start with Assets/
            if (!normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Must not contain traversal sequences
            if (normalized.Contains(".."))
            {
                return false;
            }

            // Must not contain invalid path characters
            char[] invalidChars = { ':', '*', '?', '"', '<', '>', '|' };
            foreach (char c in invalidChars)
            {
                if (normalized.IndexOf(c) >= 0)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Gets the MCP for Unity package root path.
        /// Works for registry Package Manager, local Package Manager, and Asset Store installations.
        /// </summary>
        /// <returns>The package root path (virtual for PM, absolute for Asset Store), or null if not found</returns>
        public static string GetMcpPackageRootPath()
        {
            try
            {
                // Try Package Manager first (registry and local installs)
                var packageInfo = PackageInfo.FindForAssembly(typeof(AssetPathUtility).Assembly);
                if (packageInfo != null && !string.IsNullOrEmpty(packageInfo.assetPath))
                {
                    return packageInfo.assetPath;
                }

                // Fallback to AssetDatabase for Asset Store installs (Assets/MCPForUnity)
                string[] guids = AssetDatabase.FindAssets($"t:Script {nameof(AssetPathUtility)}");

                if (guids.Length == 0)
                {
                    McpLog.Warn("Could not find AssetPathUtility script in AssetDatabase");
                    return null;
                }

                string scriptPath = AssetDatabase.GUIDToAssetPath(guids[0]);

                // Script is at: {packageRoot}/Editor/Helpers/AssetPathUtility.cs
                // Extract {packageRoot}
                int editorIndex = scriptPath.LastIndexOf("/Editor/", StringComparison.Ordinal);

                if (editorIndex >= 0)
                {
                    return scriptPath.Substring(0, editorIndex);
                }

                McpLog.Warn($"Could not determine package root from script path: {scriptPath}");
                return null;
            }
            catch (Exception ex)
            {
                McpLog.Error($"Failed to get package root path: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Reads and parses the package.json file for MCP for Unity.
        /// Handles both Package Manager (registry/local) and Asset Store installations.
        /// </summary>
        /// <returns>JObject containing package.json data, or null if not found or parse failed</returns>
        public static JObject GetPackageJson()
        {
            try
            {
                string packageRoot = GetMcpPackageRootPath();
                if (string.IsNullOrEmpty(packageRoot))
                {
                    return null;
                }

                string packageJsonPath = Path.Combine(packageRoot, "package.json");

                // Convert virtual asset path to file system path
                if (packageRoot.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                {
                    // Package Manager install - must use PackageInfo.resolvedPath
                    // Virtual paths like "Packages/..." don't work with File.Exists()
                    // Registry packages live in Library/PackageCache/package@version/
                    var packageInfo = PackageInfo.FindForAssembly(typeof(AssetPathUtility).Assembly);
                    if (packageInfo != null && !string.IsNullOrEmpty(packageInfo.resolvedPath))
                    {
                        packageJsonPath = Path.Combine(packageInfo.resolvedPath, "package.json");
                    }
                    else
                    {
                        McpLog.Warn("Could not resolve Package Manager path for package.json");
                        return null;
                    }
                }
                else if (packageRoot.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                {
                    // Asset Store install - convert to absolute file system path
                    // Application.dataPath is the absolute path to the Assets folder
                    string relativePath = packageRoot.Substring("Assets/".Length);
                    packageJsonPath = Path.Combine(Application.dataPath, relativePath, "package.json");
                }

                if (!File.Exists(packageJsonPath))
                {
                    McpLog.Warn($"package.json not found at: {packageJsonPath}");
                    return null;
                }

                string json = File.ReadAllText(packageJsonPath);
                return JObject.Parse(json);
            }
            catch (Exception ex)
            {
                McpLog.Warn($"Failed to read or parse package.json: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// The local Server/ checkout every client configuration runs, from the Server Source
        /// setting. MCP for Unity never runs a published server package, so the setting is
        /// required. A path to the repository root is corrected to its Server/ subdirectory and
        /// the correction persisted; a file:// URI resolves to its local path.
        /// MUST be called from the main thread (reads EditorPrefs).
        /// </summary>
        /// <exception cref="InvalidOperationException">Server Source is unset or not a local Server/ directory.</exception>
        public static string GetServerProjectDirectory()
        {
            return ResolveServerProjectDirectory(persistCorrection: true);
        }

        private static string ResolveServerProjectDirectory(bool persistCorrection)
        {
            string sourceOverride = EditorPrefs.GetString(EditorPrefKeys.GitUrlOverride, "").Trim();
            if (string.IsNullOrEmpty(sourceOverride))
            {
                throw new InvalidOperationException(
                    "Server Source is not set. In Advanced Settings, set it to your local unity-mcp Server/ " +
                    "directory: MCP for Unity runs only a local server checkout.");
            }

            string resolved = ResolveLocalServerPath(sourceOverride);
            ValidateLocalServerSourceOverride(sourceOverride, resolved);
            // Persist the corrected path so future reads are consistent
            if (persistCorrection && resolved != sourceOverride)
            {
                EditorPrefs.SetString(EditorPrefKeys.GitUrlOverride, resolved);
                McpLog.Info($"Auto-corrected server source override from '{sourceOverride}' to '{resolved}'");
            }

            // Validation guarantees a directory named Server, so trimming never empties a root.
            return Path.GetFullPath(GetLocalServerCheckPath(resolved))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        /// <summary>
        /// The command that runs the Server/ checkout in the environment its uv.lock pins:
        /// <c>uv run --frozen --project &lt;Server&gt; mcp-for-unity</c>. Callers append the
        /// transport arguments. MUST be called from the main thread (reads EditorPrefs).
        /// </summary>
        public static (string command, string[] args) GetServerCommand()
        {
            string projectDirectory = GetServerProjectDirectory();
            string uvxPath = MCPServiceLocator.Paths.GetUvxPath();
            if (string.IsNullOrEmpty(uvxPath))
            {
                throw new InvalidOperationException("uv not found. Install uv or set its path in Advanced Settings.");
            }

            string uvPath = new ServerCommandBuilder().BuildUvPathFromUvx(uvxPath);
            return (uvPath, new[] { "run", "--frozen", "--project", projectDirectory, "mcp-for-unity" });
        }

        /// <summary><see cref="GetServerCommand"/> with the stdio transport, for client registrations.</summary>
        public static (string command, string[] args) GetStdioServerCommand()
        {
            var (command, args) = GetServerCommand();
            return (command, args.Concat(new[] { "--transport", "stdio" }).ToArray());
        }

        /// <summary><see cref="GetStdioServerCommand"/> as one command line.</summary>
        public static string GetStdioServerCommandLine()
        {
            var (command, args) = GetStdioServerCommand();
            return JoinCommandLine(new[] { command }.Concat(args));
        }

        /// <summary>
        /// <see cref="GetServerProjectDirectory"/>, or null when Server Source is unset or invalid.
        /// Never rewrites the setting, so status reads stay side-effect free.
        /// MUST be called from the main thread (reads EditorPrefs).
        /// </summary>
        public static string TryGetServerProjectDirectory()
        {
            try
            {
                return ResolveServerProjectDirectory(persistCorrection: false);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>
        /// Joins command-line parts so CommandLineToArgvW (and .NET's argument parser) reads each
        /// back unchanged: parts that are empty or contain whitespace or quotes are quoted, embedded
        /// quotes are escaped, and backslashes are doubled where they precede a quote.
        /// </summary>
        public static string JoinCommandLine(IEnumerable<string> parts)
        {
            var line = new StringBuilder();
            foreach (string part in parts)
            {
                if (line.Length > 0)
                    line.Append(' ');

                if (part.Length > 0 && !part.Any(c => char.IsWhiteSpace(c) || c == '"'))
                {
                    line.Append(part);
                    continue;
                }

                line.Append('"');
                int backslashes = 0;
                foreach (char c in part)
                {
                    if (c == '\\')
                    {
                        backslashes++;
                        continue;
                    }

                    // Backslashes are literal unless they precede a quote.
                    line.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
                    line.Append(c);
                    backslashes = 0;
                }

                // Doubled so trailing backslashes do not escape the closing quote.
                line.Append('\\', backslashes * 2);
                line.Append('"');
            }

            return line.ToString();
        }

        /// <summary>
        /// The <c>--project</c> directory in a server command's arguments, or null when the
        /// command runs no server project (for example a published package through uvx).
        /// </summary>
        public static string ExtractServerProjectDirectory(IReadOnlyList<string> args)
        {
            if (args == null)
                return null;

            for (int i = 0; i < args.Count - 1; i++)
            {
                if (args[i] == "--project")
                    return args[i + 1];
            }

            return null;
        }

        /// <summary>
        /// Validates and auto-corrects a local server source path to ensure it points to the
        /// directory containing pyproject.toml. If the path points to a parent directory
        /// (e.g. the repo root "unity-mcp") instead of the Python package directory ("Server"),
        /// this checks for a "Server" subdirectory with pyproject.toml and returns that path.
        /// Non-local paths (URLs, PyPI references) are returned unchanged.
        /// </summary>
        internal static string ResolveLocalServerPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            // Skip non-local paths (git URLs, PyPI package names, etc.)
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("git+", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            // If it looks like a PyPI package reference (no path separators), skip
            if (!path.Contains('/') && !path.Contains('\\') && !path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            string checkPath = GetLocalServerCheckPath(path);

            // Already correct — pyproject.toml exists at this path
            if (System.IO.File.Exists(System.IO.Path.Combine(checkPath, "pyproject.toml")))
            {
                return path;
            }

            // Check if "Server" subdirectory contains pyproject.toml
            string serverSubDir = System.IO.Path.Combine(checkPath, "Server");
            if (System.IO.File.Exists(System.IO.Path.Combine(serverSubDir, "pyproject.toml")))
            {
                return path.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                    ? path.TrimEnd('/', '\\') + "/Server"
                    : serverSubDir;
            }

            // Return as-is so the caller can report one actionable override error.
            return path;
        }

        private static void ValidateLocalServerSourceOverride(string original, string resolved)
        {
            if (resolved.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                resolved.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                resolved.StartsWith("git+", StringComparison.OrdinalIgnoreCase) ||
                resolved.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            {
                throw InvalidServerSourceOverride(original);
            }

            string checkPath = GetLocalServerCheckPath(resolved);

            try
            {
                checkPath = Path.GetFullPath(checkPath);
            }
            catch (Exception)
            {
                throw InvalidServerSourceOverride(original);
            }

            if (!string.Equals(new DirectoryInfo(checkPath).Name, "Server", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(Path.Combine(checkPath, "pyproject.toml")))
            {
                throw InvalidServerSourceOverride(original);
            }
        }

        internal static string GetLocalServerCheckPath(string source)
        {
            if (!source.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                return source;
            }

            if (!Uri.TryCreate(source, UriKind.Absolute, out Uri fileUri) || !fileUri.IsFile)
            {
                return source;
            }

            // LocalPath unescapes encoded path segments and maps URI authorities to
            // UNC paths on Windows.
            return fileUri.LocalPath;
        }

        private static InvalidOperationException InvalidServerSourceOverride(string source)
        {
            return new InvalidOperationException(
                $"Server Source Override '{source}' is invalid. Set it to an explicit local Server/ " +
                "directory containing pyproject.toml.");
        }

        /// <summary>
        /// Gets the package version from package.json
        /// </summary>
        /// <returns>Version string, or "unknown" if not found</returns>
        public static string GetPackageVersion()
        {
            try
            {
                var packageJson = GetPackageJson();
                if (packageJson == null)
                {
                    return "unknown";
                }

                string version = packageJson["version"]?.ToString();
                return string.IsNullOrEmpty(version) ? "unknown" : version;
            }
            catch (Exception ex)
            {
                McpLog.Warn($"Failed to get package version: {ex.Message}");
                return "unknown";
            }
        }
    }
}
