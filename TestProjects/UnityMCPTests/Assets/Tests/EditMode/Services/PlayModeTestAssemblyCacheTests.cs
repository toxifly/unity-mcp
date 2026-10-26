using System;
using System.Collections;
using System.Reflection;
using MCPForUnity.Editor.Services;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Services
{
    /// <summary>
    /// The Test Framework's play-mode reset clears its static PlayMode assembly list instead of
    /// nulling it, and the provider takes any non-null list as loaded. With domain reload
    /// disabled, every PlayMode run after the first loaded no assemblies and ran zero tests.
    /// </summary>
    public class PlayModeTestAssemblyCacheTests
    {
        private const string PlayModeTestAssembly = "MCPForUnityTests.PlayMode";

        private FieldInfo _cache;
        private object _original;

        [SetUp]
        public void SetUp()
        {
            _cache = PlayModeTestAssemblyCache.CacheField();
            Assert.IsNotNull(_cache,
                "The Test Framework no longer has PlayerTestAssemblyProvider.m_LoadedAssemblies; "
                + "re-check whether its PlayMode cache still needs resetting.");
            _original = _cache.GetValue(null);
        }

        [TearDown]
        public void TearDown() => _cache?.SetValue(null, _original);

        [Test]
        public void AClearedCacheLoadsNoAssemblies_UntilItIsReset()
        {
            // The state the Test Framework's SubsystemRegistration reset leaves on a second entry.
            _cache.SetValue(null, Activator.CreateInstance(_cache.FieldType));
            Assert.AreEqual(0, LoadedAssemblyCount(), "the Test Framework bug this works around");

            Assert.IsTrue(PlayModeTestAssemblyCache.Reset());

            Assert.IsNull(_cache.GetValue(null));
            Assert.AreEqual(1, LoadedAssemblyCount(), "a reset cache loads the run's assemblies again");
        }

        // What PlaymodeTestsController.Run does at the start of every PlayMode run.
        private int LoadedAssemblyCount()
        {
            Assembly runner = _cache.DeclaringType.Assembly;
            object loadProxy = Activator.CreateInstance(
                runner.GetType("UnityEngine.TestTools.Utils.AssemblyLoadProxy", throwOnError: true), nonPublic: true);
            object provider = Activator.CreateInstance(
                _cache.DeclaringType,
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                args: new[] { loadProxy, new System.Collections.Generic.List<string> { PlayModeTestAssembly } },
                culture: null);
            var assemblies = (ICollection)_cache.DeclaringType.GetMethod("GetUserAssemblies").Invoke(provider, null);
            return assemblies.Count;
        }
    }
}
