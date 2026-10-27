using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MCPForUnity.Editor.Helpers;

namespace MCPForUnity.Editor.Setup
{
    /// <summary>
    /// Mirrors the unity-mcp-skill folder of the Server Source checkout into a client's skills
    /// folder, so the skill always matches the server revision. Nothing is downloaded.
    /// </summary>
    public static class SkillSyncService
    {
        private const string SkillDirectoryName = "unity-mcp-skill";
        private const string SyncOwnershipMarker = ".unity-mcp-skill-sync";

        public sealed class SyncResult
        {
            public int Added { get; set; }
            public int Updated { get; set; }
            public int Deleted { get; set; }
        }

        /// <summary>
        /// Mirrors the skill next to Server Source into <paramref name="installDir"/>.
        /// MUST be called from the main thread (reads EditorPrefs).
        /// </summary>
        public static SyncResult Sync(string installDir)
        {
            return Sync(GetSkillSourceDirectory(), installDir);
        }

        /// <summary>The unity-mcp-skill folder beside the Server Source checkout's Server/ folder.</summary>
        internal static string GetSkillSourceDirectory()
        {
            string serverDirectory = AssetPathUtility.GetServerProjectDirectory();
            string sourceDir = Path.Combine(Path.GetDirectoryName(serverDirectory), SkillDirectoryName);
            if (!File.Exists(Path.Combine(sourceDir, "SKILL.md")))
            {
                throw new InvalidOperationException(
                    $"No {SkillDirectoryName}/SKILL.md beside Server Source ({serverDirectory}). " +
                    "Server Source must be the Server/ folder of a unity-mcp checkout.");
            }

            return sourceDir;
        }

        internal static SyncResult Sync(string sourceDir, string installDir)
        {
            var sourceFiles = ListFiles(sourceDir);
            string installPath = ResolveAndValidateInstallPath(installDir);
            Directory.CreateDirectory(installPath);

            var installedFiles = ListFiles(installPath);
            var pathComparer = GetPathComparer(GetPathComparison(installPath));
            EnsureManagedInstallRoot(installPath, installedFiles.Keys, sourceFiles.Keys, pathComparer);
            var plan = BuildPlan(sourceFiles, installedFiles, pathComparer);

            foreach (var relativePath in plan.Added.Concat(plan.Updated))
            {
                string targetFile = Path.Combine(installPath, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetFile));
                File.Copy(sourceFiles[relativePath], targetFile, overwrite: true);
            }

            foreach (var relativePath in plan.Deleted)
            {
                File.Delete(installedFiles[relativePath]);
            }

            RemoveEmptyDirectories(installPath);
            return new SyncResult
            {
                Added = plan.Added.Count,
                Updated = plan.Updated.Count,
                Deleted = plan.Deleted.Count
            };
        }

        internal static SyncPlan BuildPlan(Dictionary<string, string> sourceFiles, Dictionary<string, string> installedFiles, StringComparer pathComparer)
        {
            var plan = new SyncPlan();
            var installedLookup = new Dictionary<string, string>(pathComparer);
            foreach (var installedEntry in installedFiles)
            {
                if (!installedLookup.ContainsKey(installedEntry.Key))
                {
                    installedLookup[installedEntry.Key] = installedEntry.Value;
                }
            }

            foreach (var sourceEntry in sourceFiles)
            {
                if (!installedLookup.TryGetValue(sourceEntry.Key, out var installedPath))
                {
                    plan.Added.Add(sourceEntry.Key);
                }
                else if (!File.ReadAllBytes(installedPath).AsSpan().SequenceEqual(File.ReadAllBytes(sourceEntry.Value)))
                {
                    plan.Updated.Add(sourceEntry.Key);
                }
            }

            var sourceLookup = new HashSet<string>(sourceFiles.Keys, pathComparer);
            foreach (var installedRelativePath in installedFiles.Keys)
            {
                if (!sourceLookup.Contains(installedRelativePath))
                {
                    plan.Deleted.Add(installedRelativePath);
                }
            }

            return plan;
        }

        /// <summary>Relative path (with '/' separators) to full path, excluding the ownership marker.</summary>
        internal static Dictionary<string, string> ListFiles(string root)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!Directory.Exists(root))
            {
                return map;
            }

            var normalizedRoot = Path.GetFullPath(root);
            foreach (var filePath in Directory.GetFiles(normalizedRoot, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(normalizedRoot, filePath).Replace('\\', '/');
                if (string.Equals(relativePath, SyncOwnershipMarker, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                map[relativePath] = filePath;
            }

            return map;
        }

        private static void EnsureManagedInstallRoot(
            string installPath,
            ICollection<string> installedRelativePaths,
            ICollection<string> sourceRelativePaths,
            StringComparer pathComparer)
        {
            var markerPath = Path.Combine(installPath, SyncOwnershipMarker);
            if (File.Exists(markerPath))
            {
                return;
            }

            if (installedRelativePaths.Count > 0 && !CanAdoptLegacyManagedRoot(installedRelativePaths, sourceRelativePaths, pathComparer))
            {
                throw new InvalidOperationException(
                    "Install Dir contains unmanaged files. " +
                    "Please choose an empty folder or an existing unity-mcp-skill folder.");
            }

            File.WriteAllText(markerPath, "managed-by-unity-mcp-skill-sync");
        }

        private static bool CanAdoptLegacyManagedRoot(
            ICollection<string> installedRelativePaths,
            ICollection<string> sourceRelativePaths,
            StringComparer pathComparer)
        {
            var sourceTopLevels = new HashSet<string>(sourceRelativePaths.Select(GetTopLevelSegment), pathComparer);
            var hasSkillDefinition = false;
            foreach (var installedPath in installedRelativePaths)
            {
                if (pathComparer.Equals(installedPath, "SKILL.md"))
                {
                    hasSkillDefinition = true;
                }

                if (!sourceTopLevels.Contains(GetTopLevelSegment(installedPath)))
                {
                    return false;
                }
            }

            return hasSkillDefinition;
        }

        private static string GetTopLevelSegment(string relativePath)
        {
            var separatorIndex = relativePath.IndexOf('/');
            return separatorIndex < 0 ? relativePath : relativePath.Substring(0, separatorIndex);
        }

        internal static StringComparison GetPathComparison(string root)
        {
            return IsCaseSensitiveFileSystem(root) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        }

        internal static StringComparer GetPathComparer(StringComparison pathComparison)
        {
            return pathComparison == StringComparison.Ordinal
                ? StringComparer.Ordinal
                : StringComparer.OrdinalIgnoreCase;
        }

        private static bool IsCaseSensitiveFileSystem(string root)
        {
            try
            {
                var probeName = $".mcp-case-probe-{Guid.NewGuid():N}";
                var lowercasePath = Path.Combine(root, probeName.ToLowerInvariant());
                var uppercasePath = Path.Combine(root, probeName.ToUpperInvariant());
                File.WriteAllText(lowercasePath, string.Empty);
                try
                {
                    return !File.Exists(uppercasePath);
                }
                finally
                {
                    if (File.Exists(lowercasePath))
                    {
                        File.Delete(lowercasePath);
                    }
                }
            }
            catch
            {
                return true;
            }
        }

        private static void RemoveEmptyDirectories(string root)
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            var directories = Directory.GetDirectories(root, "*", SearchOption.AllDirectories);
            Array.Sort(directories, (a, b) => string.CompareOrdinal(b, a));
            foreach (var directory in directories)
            {
                if (Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    continue;
                }

                Directory.Delete(directory, false);
            }
        }

        internal static string ResolveAndValidateInstallPath(string installDir)
        {
            if (string.IsNullOrWhiteSpace(installDir))
            {
                throw new InvalidOperationException("Install Dir is empty.");
            }

            var trimmed = installDir.Trim();
            if (trimmed.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                throw new InvalidOperationException($"Install Dir contains invalid path characters: {installDir}");
            }

            string expandedPath;
            try
            {
                expandedPath = ExpandPath(trimmed);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Install Dir is invalid and cannot be resolved: {installDir}", ex);
            }

            if (string.IsNullOrWhiteSpace(expandedPath))
            {
                throw new InvalidOperationException("Install Dir resolved to an empty path.");
            }

            return expandedPath;
        }

        internal static string ExpandPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            var expanded = path.Trim();
            if (expanded.StartsWith("~", StringComparison.Ordinal))
            {
                var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                expanded = Path.Combine(userHome, expanded.Substring(1).TrimStart('/', '\\'));
            }

            return Path.GetFullPath(expanded);
        }

        internal sealed class SyncPlan
        {
            public List<string> Added { get; } = new();
            public List<string> Updated { get; } = new();
            public List<string> Deleted { get; } = new();
        }
    }
}
