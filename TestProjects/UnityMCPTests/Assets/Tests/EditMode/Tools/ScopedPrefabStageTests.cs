using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MCPForUnityTests.Editor.Tools
{
    public sealed class ScopedPrefabStageTests
    {
        [Test]
        public void ScopedSavePersistsAnOpenPrefabStage()
        {
            string path = $"Assets/ScopedPrefabStageTest-{Guid.NewGuid():N}.prefab";
            Assert.IsNull(PrefabStageUtility.GetCurrentPrefabStage());
            try
            {
                var root = new GameObject("ScopedStageRoot");
                try
                {
                    Assert.IsNotNull(PrefabUtility.SaveAsPrefabAsset(root, path));
                }
                finally { UnityEngine.Object.DestroyImmediate(root); }
                var stage = PrefabStageUtility.OpenPrefab(path);
                var child = new GameObject("ScopedStageChild");
                child.transform.SetParent(stage.prefabContentsRoot.transform, false);
                EditorSceneManager.MarkSceneDirty(stage.scene);
                Type tool = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("MCPForUnity.Editor.Tools.SavePrefabScoped"))
                    .First(type => type != null);
                object result = tool.GetMethod("HandleCommand").Invoke(null,
                    new object[] { new JObject { ["prefabPath"] = path } });
                Assert.IsTrue(JObject.FromObject(result).Value<bool>("success"), result.ToString());
                stage.ClearDirtiness();
                StageUtility.GoToMainStage();
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<GameObject>(path).transform.Find("ScopedStageChild"));
            }
            finally
            {
                var stage = PrefabStageUtility.GetCurrentPrefabStage();
                if (stage != null)
                {
                    stage.ClearDirtiness();
                    StageUtility.GoToMainStage();
                }
                AssetDatabase.DeleteAsset(path);
            }
        }
    }
}
