using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using MCPForUnity.Editor.Tools;
using static MCPForUnityTests.Editor.TestUtilities;

namespace MCPForUnityTests.Editor.Tools
{
    public class ManageSceneCreateTests
    {
        private const string TempSceneDir = "Assets/Temp/ManageSceneCreateTests";
        private string _scenePath;

        [TearDown]
        public void TearDown()
        {
            SafeDeleteAsset(_scenePath);

            if (AssetDatabase.IsValidFolder(TempSceneDir))
            {
                AssetDatabase.DeleteAsset(TempSceneDir);
            }
            CleanupEmptyParentFolders(TempSceneDir);

            _scenePath = null;
        }

        [Test]
        public void Create_WithFilePath_DoesNotCreateUnityFolder()
        {
            EnsureFolder(TempSceneDir);

            _scenePath = Path.Combine(TempSceneDir, "SceneCreatePath.unity").Replace('\\', '/');
            var result = ToJObject(ManageScene.HandleCommand(new JObject
            {
                ["action"] = "create",
                ["name"] = "SceneCreatePath",
                ["path"] = _scenePath,
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());

            var data = result["data"] as JObject;
            Assert.IsNotNull(data, "Expected data payload.");
            Assert.AreEqual(_scenePath, data.Value<string>("path"), "Returned scene path should match the requested file path.");

            Assert.IsFalse(AssetDatabase.IsValidFolder(_scenePath), "Scene path should be a file, not a folder.");
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<SceneAsset>(_scenePath), "Scene asset should exist at the expected file path.");
        }
    }
}

