using MCPForUnity.Editor.Services;

namespace MCPForUnityTests.Editor
{
    /// <summary>Path resolver that reports a fixed uvx path and no other tools.</summary>
    public sealed class FixedUvxPathResolver : IPathResolverService
    {
        private readonly string _uvxPath;

        public FixedUvxPathResolver(string uvxPath) => _uvxPath = uvxPath;

        public string GetUvxPath() => _uvxPath;
        public string GetClaudeCliPath() => null;
        public bool IsPythonDetected() => false;
        public bool IsClaudeCliDetected() => false;
        public void SetUvxPathOverride(string path) { }
        public void SetClaudeCliPathOverride(string path) { }
        public void ClearUvxPathOverride() { }
        public void ClearClaudeCliPathOverride() { }
        public bool HasUvxPathOverride => false;
        public bool HasClaudeCliPathOverride => false;
        public bool HasUvxPathFallback => false;

        public bool TryValidateUvxExecutable(string uvPath, out string version)
        {
            version = null;
            return false;
        }
    }
}
