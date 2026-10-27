using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Services;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using UnityEditor.TestTools.TestRunner.Api;
using RunState = UnityEditor.TestTools.TestRunner.Api.RunState;

namespace MCPForUnityTests.Editor.Services
{
    /// <summary>
    /// NUnit runs an [Explicit] test whenever the filter matches the test itself, and the Test
    /// Framework's assembly filter matches every test in its assembly: an assembly_names run used
    /// to execute the explicit tests a full run skips. A filter that names no test now excludes
    /// them by name; naming a test, group or category still opts one in.
    /// </summary>
    public class ExplicitTestExclusionTests
    {
        [Test]
        public void AnAssemblyOnlySelectionNamesNoTest()
        {
            Assert.IsTrue(ExplicitTestExclusion.NamesNoTest(new TestFilterOptions { AssemblyNames = new[] { "A" } }));
        }

        [Test]
        public void AnExclusionOnlySelectionNamesNoTest()
        {
            Assert.IsTrue(ExplicitTestExclusion.NamesNoTest(new TestFilterOptions { TestNames = new[] { "!N.C.T" } }));
            Assert.IsTrue(ExplicitTestExclusion.NamesNoTest(new TestFilterOptions { CategoryNames = new[] { "!Slow" } }));
        }

        [Test]
        public void NamingATestGroupOrCategoryOptsExplicitTestsIn()
        {
            string[] assembly = { "A" };
            Assert.IsFalse(ExplicitTestExclusion.NamesNoTest(new TestFilterOptions { AssemblyNames = assembly, TestNames = new[] { "N.C.T" } }));
            Assert.IsFalse(ExplicitTestExclusion.NamesNoTest(new TestFilterOptions { AssemblyNames = assembly, GroupNames = new[] { "^N\\." } }));
            Assert.IsFalse(ExplicitTestExclusion.NamesNoTest(new TestFilterOptions { AssemblyNames = assembly, CategoryNames = new[] { "OnlineE2E" } }));
        }

        [Test]
        public void NoFilterIsLeftToNUnit()
        {
            // An empty NUnit filter already skips explicit tests; no tree lookup is needed.
            Assert.IsFalse(ExplicitTestExclusion.NamesNoTest(null));
            Assert.IsFalse(ExplicitTestExclusion.NamesNoTest(new TestFilterOptions { AssemblyNames = new[] { " " } }));
        }

        [Test]
        public void OnlyANamedTestGroupOrCategoryNamesTests()
        {
            // The test-stage guard counts exactly these as focused: an exclusion-only filter runs
            // everything else, so it cannot stand in for a focused pass.
            Assert.IsFalse(ExplicitTestExclusion.NamesTests(null));
            Assert.IsFalse(ExplicitTestExclusion.NamesTests(new TestFilterOptions { AssemblyNames = new[] { "A" } }));
            Assert.IsFalse(ExplicitTestExclusion.NamesTests(new TestFilterOptions { TestNames = new[] { "!N.C.T" }, CategoryNames = new[] { "!Slow", " " } }));
            Assert.IsTrue(ExplicitTestExclusion.NamesTests(new TestFilterOptions { TestNames = new[] { "!N.C.U", "N.C.T" } }));
            Assert.IsTrue(ExplicitTestExclusion.NamesTests(new TestFilterOptions { GroupNames = new[] { "^N\\." } }));
            Assert.IsTrue(ExplicitTestExclusion.NamesTests(new TestFilterOptions { CategoryNames = new[] { "Fast" } }));
        }

        [Test]
        public void ExplicitNodesAreExcludedOutermostFirstAndExistingExclusionsKept()
        {
            FakeTest root = Node("A.dll", RunState.Runnable,
                Node("N.Runnable", RunState.Runnable,
                    Node("N.Runnable.Plain", RunState.Runnable),
                    Node("N.Runnable.Opt", RunState.Explicit)),
                Node("N.ExplicitFixture", RunState.Explicit,
                    Node("N.ExplicitFixture.Inner", RunState.Explicit)));
            var filter = new TestFilterOptions { AssemblyNames = new[] { "A" }, TestNames = new[] { "!N.Skip" } };

            string[] names = ExplicitTestExclusion.TestNamesExcludingExplicit(filter, root);

            CollectionAssert.AreEqual(new[] { "!N.Skip", "!N.Runnable.Opt", "!N.ExplicitFixture" }, names);
        }

        [Test]
        public void ATreeWithoutExplicitTestsAddsNoTestNames()
        {
            FakeTest root = Node("A.dll", RunState.Runnable, Node("N.C", RunState.Runnable, Node("N.C.T", RunState.Runnable)));

            Assert.IsNull(ExplicitTestExclusion.TestNamesExcludingExplicit(new TestFilterOptions { AssemblyNames = new[] { "A" } }, root));
        }

        private static FakeTest Node(string fullName, RunState runState, params FakeTest[] children) =>
            new FakeTest(fullName, runState, children);

        private sealed class FakeTest : ITestAdaptor
        {
            private readonly List<ITestAdaptor> _children;

            public FakeTest(string fullName, RunState runState, IEnumerable<FakeTest> children)
            {
                FullName = fullName;
                RunState = runState;
                _children = children.Cast<ITestAdaptor>().ToList();
            }

            public string Id => FullName;
            public string Name => FullName;
            public string FullName { get; }
            public int TestCaseCount => _children.Count;
            public bool HasChildren => _children.Count > 0;
            public bool IsSuite => HasChildren;
            public IEnumerable<ITestAdaptor> Children => _children;
            public ITestAdaptor Parent => null;
            public int TestCaseTimeout => 0;
            public ITypeInfo TypeInfo => null;
            public IMethodInfo Method => null;
            public object[] Arguments => null;
            public string[] Categories => new string[0];
            public bool IsTestAssembly => false;
            public RunState RunState { get; }
            public string Description => null;
            public string SkipReason => null;
            public string ParentId => null;
            public string ParentFullName => null;
            public string UniqueName => FullName;
            public string ParentUniqueName => null;
            public int ChildIndex => -1;
            public TestMode TestMode => TestMode.EditMode;
        }
    }
}
