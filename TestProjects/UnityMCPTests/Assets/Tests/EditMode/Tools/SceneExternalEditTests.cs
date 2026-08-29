using System;
using System.IO;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MCPForUnity.Tests.EditMode.Tools
{
    /// <summary>
    /// Covers manage_scene's apply_external_edit transaction, including fallback file replacement
    /// and recovery of a target scene that was open when the transaction began.
    /// </summary>
    [TestFixture]
    public class SceneExternalEditTests
    {
        private const string SourceScene = "Assets/Scenes/SampleScene.unity";
        private const string TempScene = "Assets/Scenes/SceneExternalEditTemp.unity";
        private const string Anchor = "m_HaloStrength: 0.5";

        private string FullPath => Path.Combine(
            Application.dataPath.Substring(0, Application.dataPath.Length - "Assets".Length),
            TempScene);

        [SetUp]
        public void SetUp()
        {
            SceneExternalEdit.ReplaceFileOverrideForTests = null;
            SceneExternalEdit.MoveFileOverrideForTests = null;
            SceneExternalEdit.GetFileAttributesOverrideForTests = null;
            AssetDatabase.DeleteAsset(TempScene);
            Assert.IsTrue(
                AssetDatabase.CopyAsset(SourceScene, TempScene),
                $"Could not copy '{SourceScene}' to '{TempScene}'.");
        }

        [TearDown]
        public void TearDown()
        {
            SceneExternalEdit.ReplaceFileOverrideForTests = null;
            SceneExternalEdit.MoveFileOverrideForTests = null;
            SceneExternalEdit.GetFileAttributesOverrideForTests = null;
            AssetDatabase.DeleteAsset(TempScene);
        }

        [Test]
        public void AppliesEveryAnchoredEdit_AndDoesNotReloadASceneThatIsNotOpen()
        {
            var result = Run(Edits(Anchor, "m_HaloStrength: 0.25"));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.IsFalse(result["data"].Value<bool>("reopened"));
            StringAssert.Contains("m_HaloStrength: 0.25", File.ReadAllText(FullPath));
        }

        [Test]
        public void ACountMismatchWritesNothingAtAll()
        {
            // A partial write is the dangerous outcome: the file would be half-migrated with no
            // error the caller could act on, so the whole call has to fail before touching disk.
            byte[] before = File.ReadAllBytes(FullPath);

            var result = Run(Edits("m_HaloStrength: this anchor does not exist", "anything"));

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            CollectionAssert.AreEqual(before, File.ReadAllBytes(FullPath));
        }

        [Test]
        public void ASecondEditFailingRollsBackTheFirstOne()
        {
            byte[] before = File.ReadAllBytes(FullPath);

            var edits = new JArray
            {
                new JObject { ["old_text"] = Anchor, ["new_text"] = "m_HaloStrength: 0.25" },
                new JObject { ["old_text"] = "absent anchor", ["new_text"] = "x" },
            };

            var result = Run(edits);

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            CollectionAssert.AreEqual(before, File.ReadAllBytes(FullPath));
        }

        [Test]
        public void ADryRunReportsTheMatchWithoutWriting()
        {
            byte[] before = File.ReadAllBytes(FullPath);

            var p = Params(Edits(Anchor, "m_HaloStrength: 0.25"));
            p["dry_run"] = true;
            var result = Invoke(p);

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.IsTrue(result["data"].Value<bool>("dryRun"));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(FullPath));
        }

        [Test]
        public void NoEditsIsAPlainResyncRatherThanAnError()
        {
            // The rescue path: the file was already changed by something else and the Editor only
            // needs to be brought back in line with it.
            var result = Invoke(Params(null));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
        }

        [Test]
        public void UnsupportedAtomicReplaceFallsBackAndWritesRequestedBytes()
        {
            SceneExternalEdit.ReplaceFileOverrideForTests = (_, __, ___) =>
                throw new PlatformNotSupportedException("simulated unsupported filesystem");

            var result = Run(Edits(Anchor, "m_HaloStrength: 0.25"));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            StringAssert.Contains("m_HaloStrength: 0.25", File.ReadAllText(FullPath));
            AssertNoTransactionFilesRemain();
        }

        [Test]
        public void FallbackMoveFailureRestoresTheOriginalBytes()
        {
            byte[] before = File.ReadAllBytes(FullPath);
            int moveCount = 0;
            SceneExternalEdit.ReplaceFileOverrideForTests = (_, __, ___) =>
                throw new PlatformNotSupportedException("simulated unsupported filesystem");
            SceneExternalEdit.MoveFileOverrideForTests = (source, destination) =>
            {
                moveCount++;
                if (moveCount == 2)
                {
                    throw new IOException("simulated fallback move failure");
                }
                File.Move(source, destination);
            };

            var result = Run(Edits(Anchor, "m_HaloStrength: 0.25"));

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            CollectionAssert.AreEqual(before, File.ReadAllBytes(FullPath));
            Assert.AreEqual(3, moveCount, "The third move should restore the original from backup.");
            AssertNoTransactionFilesRemain();
        }

        [Test]
        public void FallbackFailureReopensAnOpenTargetScene()
        {
            if (SceneManager.loadedSceneCount != 1 || SceneManager.GetActiveScene().isDirty)
            {
                Assert.Ignore("Test requires one clean scene so it can restore the prior Editor state.");
            }

            string previousScenePath = SceneManager.GetActiveScene().path;
            byte[] before = File.ReadAllBytes(FullPath);
            int moveCount = 0;
            try
            {
                EditorSceneManager.OpenScene(TempScene, OpenSceneMode.Single);
                SceneExternalEdit.ReplaceFileOverrideForTests = (_, __, ___) =>
                    throw new PlatformNotSupportedException("simulated unsupported filesystem");
                SceneExternalEdit.MoveFileOverrideForTests = (source, destination) =>
                {
                    moveCount++;
                    if (moveCount == 2)
                    {
                        throw new IOException("simulated fallback move failure");
                    }
                    File.Move(source, destination);
                };

                var result = Run(Edits(Anchor, "m_HaloStrength: 0.25"));

                Assert.IsFalse(result.Value<bool>("success"), result.ToString());
                Assert.AreEqual(TempScene, SceneManager.GetActiveScene().path);
                CollectionAssert.AreEqual(before, File.ReadAllBytes(FullPath));
                AssertNoTransactionFilesRemain();
            }
            finally
            {
                SceneExternalEdit.ReplaceFileOverrideForTests = null;
                SceneExternalEdit.MoveFileOverrideForTests = null;
                if (string.IsNullOrEmpty(previousScenePath))
                {
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                }
                else
                {
                    EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);
                }
            }
        }

        [Test]
        public void APathThatIsNotASceneAssetIsRejected()
        {
            var p = new JObject
            {
                ["action"] = "apply_external_edit",
                ["path"] = "Assets/Scenes/SampleScene.prefab",
            };

            Assert.IsFalse(Invoke(p).Value<bool>("success"));
        }

        [Test]
        public void AMissingSceneFileIsRejected()
        {
            var p = new JObject
            {
                ["action"] = "apply_external_edit",
                ["path"] = "Assets/Scenes/NoSuchScene_99999.unity",
            };

            Assert.IsFalse(Invoke(p).Value<bool>("success"));
        }

        [TestCase("../../OtherProject/Assets/Main.unity")]
        [TestCase("Assets/../Outside.unity")]
        [TestCase("Assets/Scenes/../Scenes/SceneExternalEditTemp.unity")]
        public void APathContainingTraversalSegmentsIsRejected(string path)
        {
            var result = Invoke(new JObject
            {
                ["action"] = "apply_external_edit",
                ["path"] = path,
            });

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            StringAssert.Contains("traversal", result.Value<string>("error"));
        }

        [Test]
        public void AnAbsolutePathIsRejectedEvenWhenItPointsInsideAssets()
        {
            var result = Invoke(new JObject
            {
                ["action"] = "apply_external_edit",
                ["path"] = FullPath,
            });

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            StringAssert.Contains("relative", result.Value<string>("error"));
        }

        [Test]
        public void ASceneUnderAReparsePointDirectoryIsRejectedBeforeReadingOrWriting()
        {
            byte[] before = File.ReadAllBytes(FullPath);
            string linkedDirectory = Path.GetFullPath(Path.GetDirectoryName(FullPath));
            StringComparison comparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            SceneExternalEdit.GetFileAttributesOverrideForTests = path =>
            {
                if (string.Equals(Path.GetFullPath(path), linkedDirectory, comparison))
                {
                    return FileAttributes.Directory | FileAttributes.ReparsePoint;
                }
                return File.GetAttributes(path);
            };

            var result = Run(Edits(Anchor, "m_HaloStrength: 0.25"));

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            StringAssert.Contains("symbolic link or junction", result.Value<string>("error"));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(FullPath));
        }

        [Test]
        public void AnUnloadedAdditiveSceneStillBlocksSingleSceneReplacement()
        {
            if (SceneManager.loadedSceneCount != 1 || SceneManager.GetActiveScene().isDirty)
            {
                Assert.Ignore("Test requires one clean scene so it can restore the prior Editor state.");
            }

            string previousScenePath = SceneManager.GetActiveScene().path;
            byte[] before = File.ReadAllBytes(FullPath);
            try
            {
                EditorSceneManager.OpenScene(TempScene, OpenSceneMode.Single);
                Scene additive = EditorSceneManager.OpenScene(SourceScene, OpenSceneMode.Additive);
                Assert.IsTrue(EditorSceneManager.CloseScene(additive, false));
                Assert.AreEqual(2, SceneManager.sceneCount, "The unloaded scene must remain in the hierarchy.");
                Assert.AreEqual(1, SceneManager.loadedSceneCount);

                var result = Run(Edits(Anchor, "m_HaloStrength: 0.25"));

                Assert.IsFalse(result.Value<bool>("success"), result.ToString());
                StringAssert.Contains("remove_scene=true", result.Value<string>("error"));
                CollectionAssert.AreEqual(before, File.ReadAllBytes(FullPath));
                Assert.AreEqual(2, SceneManager.sceneCount, "The additive scene setup must be preserved.");
            }
            finally
            {
                if (string.IsNullOrEmpty(previousScenePath))
                {
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                }
                else
                {
                    EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);
                }
            }
        }

        [TestCase("Scenes\\SceneExternalEditTemp.unity")]
        [TestCase("aSsEtS\\Scenes\\SceneExternalEditTemp.unity")]
        public void AValidRelativePathIsCanonicalizedToAUnityAssetPath(string path)
        {
            var p = new JObject
            {
                ["action"] = "apply_external_edit",
                ["path"] = path,
                ["dry_run"] = true,
            };

            var result = Invoke(p);

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual(TempScene, result["data"].Value<string>("path"));
        }

        private static JArray Edits(string oldText, string newText) => new JArray
        {
            new JObject { ["old_text"] = oldText, ["new_text"] = newText },
        };

        private static JObject Params(JArray edits)
        {
            var p = new JObject
            {
                ["action"] = "apply_external_edit",
                ["path"] = TempScene,
            };
            if (edits != null)
            {
                p["edits"] = edits;
            }
            return p;
        }

        private static JObject Run(JArray edits) => Invoke(Params(edits));

        private void AssertNoTransactionFilesRemain()
        {
            string directory = Path.GetDirectoryName(FullPath);
            string pattern = Path.GetFileName(FullPath) + ".mcp-*";
            Assert.IsEmpty(Directory.GetFiles(directory, pattern));
        }

        private static JObject Invoke(JObject p)
        {
            object result = ManageScene.HandleCommand(p);
            return result as JObject ?? JObject.FromObject(result);
        }
    }
}
