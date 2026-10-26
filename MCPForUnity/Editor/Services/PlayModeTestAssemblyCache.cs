using System;
using System.Reflection;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// The Unity Test Framework keeps the PlayMode test assemblies in a static list that only a
    /// domain reload drops (<c>PlayerTestAssemblyProvider.m_LoadedAssemblies</c>). Its play-mode
    /// reset clears that list instead of nulling it, and the provider takes any non-null list as
    /// already loaded. With domain reload disabled, every PlayMode run after the first therefore
    /// loads no assemblies and runs zero tests. Nulling the list before a run makes the runner
    /// load that run's assemblies again.
    /// </summary>
    internal static class PlayModeTestAssemblyCache
    {
        internal const string ProviderTypeName =
            "UnityEngine.TestTools.Utils.PlayerTestAssemblyProvider, UnityEngine.TestRunner";
        internal const string CacheFieldName = "m_LoadedAssemblies";

        /// <summary>
        /// Returns false when this Test Framework version has no such cache; the zero-executed
        /// check in <see cref="TestJobManager"/> still keeps an empty run from passing.
        /// </summary>
        internal static bool Reset()
        {
            FieldInfo cache = CacheField();
            if (cache == null)
            {
                return false;
            }

            cache.SetValue(null, null);
            return true;
        }

        internal static FieldInfo CacheField() =>
            Type.GetType(ProviderTypeName)?.GetField(CacheFieldName, BindingFlags.NonPublic | BindingFlags.Static);
    }
}
