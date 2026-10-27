using System;
using System.IO;
using MCPForUnity.Editor.Setup;
using NUnit.Framework;

namespace MCPForUnityTests.Editor
{
    public class SkillSyncServiceTests
    {
        private string _root;
        private string _source;
        private string _install;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), $"skill-sync-{Guid.NewGuid():N}");
            _source = Path.Combine(_root, "unity-mcp-skill");
            _install = Path.Combine(_root, "install", "unity-mcp-skill");
            Write(_source, "SKILL.md", "skill");
            Write(_source, "references/tools.md", "tools");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        [Test]
        public void Sync_CopiesSkillIntoEmptyInstallDir()
        {
            var result = SkillSyncService.Sync(_source, _install);

            Assert.AreEqual(2, result.Added);
            Assert.AreEqual("skill", File.ReadAllText(Path.Combine(_install, "SKILL.md")));
            Assert.AreEqual("tools", File.ReadAllText(Path.Combine(_install, "references", "tools.md")));
        }

        [Test]
        public void Sync_MirrorsChangedAndRemovedFiles()
        {
            SkillSyncService.Sync(_source, _install);
            Write(_source, "SKILL.md", "skill v2");
            File.Delete(Path.Combine(_source, "references", "tools.md"));

            var result = SkillSyncService.Sync(_source, _install);

            Assert.AreEqual((0, 1, 1), (result.Added, result.Updated, result.Deleted));
            Assert.AreEqual("skill v2", File.ReadAllText(Path.Combine(_install, "SKILL.md")));
            Assert.IsFalse(Directory.Exists(Path.Combine(_install, "references")));
        }

        [Test]
        public void Sync_UnchangedSkill_ReportsNoChanges()
        {
            SkillSyncService.Sync(_source, _install);

            var result = SkillSyncService.Sync(_source, _install);

            Assert.AreEqual((0, 0, 0), (result.Added, result.Updated, result.Deleted));
        }

        [Test]
        public void Sync_AdoptsExistingSkillInstall()
        {
            Write(_install, "SKILL.md", "old skill");

            var result = SkillSyncService.Sync(_source, _install);

            Assert.AreEqual((1, 1, 0), (result.Added, result.Updated, result.Deleted));
            Assert.AreEqual("skill", File.ReadAllText(Path.Combine(_install, "SKILL.md")));
        }

        [Test]
        public void Sync_RefusesInstallDirWithUnmanagedFiles()
        {
            Write(_install, "notes.txt", "mine");

            var error = Assert.Throws<InvalidOperationException>(() => SkillSyncService.Sync(_source, _install));

            StringAssert.Contains("unmanaged files", error.Message);
            Assert.AreEqual("mine", File.ReadAllText(Path.Combine(_install, "notes.txt")));
            Assert.IsFalse(File.Exists(Path.Combine(_install, "SKILL.md")));
        }

        [Test]
        public void GetSkillSourceDirectory_UsesSkillBesideServerSource()
        {
            using var source = new TempServerSource();
            Write(source.Root, "unity-mcp-skill/SKILL.md", "skill");

            Assert.AreEqual(Path.Combine(source.Root, "unity-mcp-skill"), SkillSyncService.GetSkillSourceDirectory());
        }

        [Test]
        public void GetSkillSourceDirectory_WithoutSkill_Throws()
        {
            using var source = new TempServerSource();

            var error = Assert.Throws<InvalidOperationException>(() => SkillSyncService.GetSkillSourceDirectory());
            StringAssert.Contains("unity-mcp-skill/SKILL.md", error.Message);
        }

        private static void Write(string root, string relativePath, string content)
        {
            string path = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
        }
    }
}
