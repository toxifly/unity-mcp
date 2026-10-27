using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.TestTools.TestRunner.Api;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// NUnit runs an [Explicit] test whenever the filter matches the test itself, and the Test
    /// Framework's assembly filter matches every test in the assembly (an exclusion matches
    /// everything it does not exclude). A filter that names no test, group or category is a whole
    /// suite, so it would run the explicit tests a plain full run skips. Such a filter gets those
    /// tests excluded by name; naming a test, a group or a category is still how an explicit test
    /// is opted into.
    /// </summary>
    internal static class ExplicitTestExclusion
    {
        internal static bool NamesNoTest(TestFilterOptions filter) =>
            filter != null
            && (Any(filter.AssemblyNames) || Any(filter.TestNames) || Any(filter.GroupNames) || Any(filter.CategoryNames))
            && !NamesTests(filter);

        /// <summary>
        /// True when the filter names a test, group or category to run. Exclusions alone run
        /// everything else, so they name nothing; the test-stage guard counts only this as focused.
        /// </summary>
        internal static bool NamesTests(TestFilterOptions filter) =>
            filter != null
            && (Includes(filter.TestNames) || Includes(filter.GroupNames) || Includes(filter.CategoryNames));

        /// <summary>The selection's test names plus a "!" exclusion for each explicit test.</summary>
        internal static string[] TestNamesExcludingExplicit(TestFilterOptions filter, ITestAdaptor root)
        {
            var names = new List<string>(filter?.TestNames ?? Array.Empty<string>());
            names.AddRange(ExplicitFullNames(root).Select(name => "!" + name));
            return names.Count == 0 ? null : names.ToArray();
        }

        /// <summary>Full names of the outermost explicit nodes; their children go with them.</summary>
        internal static List<string> ExplicitFullNames(ITestAdaptor root)
        {
            var names = new List<string>();
            Collect(root, names);
            return names;
        }

        private static void Collect(ITestAdaptor node, List<string> names)
        {
            if (node == null)
            {
                return;
            }

            if (node.RunState == RunState.Explicit && !string.IsNullOrEmpty(node.FullName))
            {
                names.Add(node.FullName);
                return;
            }

            if (node.HasChildren && node.Children != null)
            {
                foreach (ITestAdaptor child in node.Children)
                {
                    Collect(child, names);
                }
            }
        }

        private static bool Any(string[] values) =>
            values != null && values.Any(v => !string.IsNullOrWhiteSpace(v));

        private static bool Includes(string[] values) =>
            values != null && values.Any(v => !string.IsNullOrWhiteSpace(v) && !v.StartsWith("!", StringComparison.Ordinal));
    }
}
