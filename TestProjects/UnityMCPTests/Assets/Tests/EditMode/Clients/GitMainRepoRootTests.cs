using System.IO;
using MCPForUnity.Editor.Clients;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Clients
{
    /// <summary>
    /// `claude mcp add --scope local` keys a worktree's registration by the main repo root,
    /// which CheckStatus reads from the worktree's .git pointer file.
    /// </summary>
    [TestFixture]
    public class GitMainRepoRootTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "mcp-gitroot-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private string MakeWorktree()
        {
            string worktree = Path.Combine(_root, "wt");
            Directory.CreateDirectory(worktree);
            File.WriteAllText(Path.Combine(worktree, ".git"),
                "gitdir: " + Path.Combine(_root, "main", ".git", "worktrees", "wt") + "\n");
            return worktree;
        }

        private string Expected => Path.Combine(_root, "main").Replace('\\', '/');

        [Test]
        public void ProjectAtWorktreeRoot_ResolvesMainRoot()
        {
            Assert.AreEqual(Expected, ClaudeCliMcpConfigurator.GetGitMainRepoRoot(MakeWorktree()));
        }

        [Test]
        public void ProjectInWorktreeSubfolder_ResolvesMainRoot()
        {
            string project = Path.Combine(MakeWorktree(), "TestProjects", "UnityMCPTests");
            Directory.CreateDirectory(project);

            Assert.AreEqual(Expected, ClaudeCliMcpConfigurator.GetGitMainRepoRoot(project));
        }

        [Test]
        public void RegularCheckout_ReturnsNull()
        {
            Directory.CreateDirectory(Path.Combine(_root, ".git"));
            string project = Path.Combine(_root, "Game");
            Directory.CreateDirectory(project);

            Assert.IsNull(ClaudeCliMcpConfigurator.GetGitMainRepoRoot(project));
        }
    }
}
