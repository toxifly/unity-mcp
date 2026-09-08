using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MCPForUnityTests.Editor.Services
{
    public class McpObjectHandleTests
    {
        private MethodInfo _get;
        private MethodInfo _resolve;

        [SetUp]
        public void SetUp()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("MCPForUnity.Runtime.Helpers.UnityObjectIdCompat"))
                .First(t => t != null);
            _get = type.GetMethod("GetInstanceIDCompat");
            _resolve = type.GetMethod("InstanceIDToObjectCompat");
        }

        [Test]
        public void ObjectAndComponentRoundTripWithDistinctStableHandles()
        {
            var go = new GameObject("MCP handle test");
            try
            {
                int handle = Handle(go);
                int componentHandle = Handle(go.transform);
                Assert.AreNotEqual(0, handle);
                Assert.AreNotEqual(handle, componentHandle);
                Assert.AreEqual(handle, Handle(go));
                Assert.AreSame(go, Resolve(handle));
                Assert.AreSame(go.transform, Resolve(componentHandle));
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void DestroyedObjectDoesNotResolveOrAliasANewObject()
        {
            var first = new GameObject("MCP old handle");
            int oldHandle = Handle(first);
            Object.DestroyImmediate(first);
            var second = new GameObject("MCP new handle");
            try
            {
                Assert.IsTrue(Resolve(oldHandle) == null);
                Assert.AreNotEqual(oldHandle, Handle(second));
                Assert.AreSame(second, Resolve(Handle(second)));
            }
            finally { Object.DestroyImmediate(second); }
        }

        [Test]
        public void NullAndUnknownHandlesResolveToNull()
        {
            Assert.AreEqual(0, Handle(null));
            Assert.IsNull(Resolve(0));
            Assert.IsNull(Resolve(int.MaxValue));
        }

        private int Handle(Object value) => (int)_get.Invoke(null, new object[] { value });
        private Object Resolve(int handle) => (Object)_resolve.Invoke(null, new object[] { handle });
    }
}
