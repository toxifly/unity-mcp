using System;
using System.IO;
using MCPForUnity.Editor.Constants;
using UnityEditor;

namespace MCPForUnityTests.Editor
{
    /// <summary>
    /// Points Server Source at a throwaway Server/ checkout (a directory holding pyproject.toml)
    /// and restores the previous setting on dispose.
    /// </summary>
    public sealed class TempServerSource : IDisposable
    {
        private readonly bool _hadOverride;
        private readonly string _originalOverride;

        public string Root { get; }
        public string ServerDirectory { get; }

        public TempServerSource(string rootName = null)
        {
            _hadOverride = EditorPrefs.HasKey(EditorPrefKeys.GitUrlOverride);
            _originalOverride = EditorPrefs.GetString(EditorPrefKeys.GitUrlOverride, string.Empty);

            Root = Path.Combine(Path.GetTempPath(), rootName ?? $"unity-mcp-source-{Guid.NewGuid():N}");
            ServerDirectory = Path.GetFullPath(Path.Combine(Root, "Server"));
            Directory.CreateDirectory(ServerDirectory);
            File.WriteAllText(Path.Combine(ServerDirectory, "pyproject.toml"), "[project]\nname = \"mcpforunityserver\"\n");
            EditorPrefs.SetString(EditorPrefKeys.GitUrlOverride, ServerDirectory);
        }

        public void Dispose()
        {
            if (_hadOverride)
                EditorPrefs.SetString(EditorPrefKeys.GitUrlOverride, _originalOverride);
            else
                EditorPrefs.DeleteKey(EditorPrefKeys.GitUrlOverride);

            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
