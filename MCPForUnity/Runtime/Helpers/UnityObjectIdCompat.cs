using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif
using Object = UnityEngine.Object;

namespace MCPForUnity.Runtime.Helpers
{
    // Part of MCP for Unity's compat-shim family. See UnityCompatShims.cs in this
    // folder for the full list of shims, the audit policy, and the reflection pattern.
    /// <summary>Session-scoped object handles for MCP's int-shaped wire format.</summary>
    public static class UnityObjectIdCompat
    {
#if UNITY_6000_6_OR_NEWER
        private const string Prefix = "MCPForUnity.ObjectHandles.v1.";
        // Lookups hit these first; in the Editor, SessionState only carries the table
        // across domain reloads and is touched on a miss or a new handle.
        private static readonly Dictionary<ulong, int> Handles = new Dictionary<ulong, int>();
#if UNITY_EDITOR
        private static readonly Dictionary<int, ulong> Entities = new Dictionary<int, ulong>();
#else
        private static int _nextHandle;
#endif
#endif

        /// <summary>
        /// Returns an opaque, nonzero handle. Unity 6.6 entity IDs retain all 64 bits
        /// in a mapping; handles must never be cast directly to native entity IDs.
        /// Editor mappings survive domain reloads through SessionState.
        /// </summary>
        public static int GetInstanceIDCompat(this Object obj)
        {
            if (obj == null) return 0;
#if UNITY_6000_6_OR_NEWER
            ulong entityId = EntityId.ToULong(obj.GetEntityId());
            if (Handles.TryGetValue(entityId, out int handle)) return handle;
#if UNITY_EDITOR
            string entityKey = entityId.ToString(CultureInfo.InvariantCulture);
            handle = SessionState.GetInt(Prefix + "entity." + entityKey, 0);
            if (handle == 0)
            {
                handle = checked(SessionState.GetInt(Prefix + "next", 0) + 1);
                SessionState.SetInt(Prefix + "next", handle);
                SessionState.SetString(Prefix + "handle." + handle, entityKey);
                SessionState.SetInt(Prefix + "entity." + entityKey, handle);
            }
            Entities[handle] = entityId;
#else
            handle = checked(++_nextHandle);
#endif
            Handles[entityId] = handle;
            return handle;
#elif UNITY_6000_5_OR_NEWER
            return (int)EntityId.ToULong(obj.GetEntityId());
#else
            return obj.GetInstanceID();
#endif
        }

#if UNITY_EDITOR
        /// <summary>Resolves an issued handle, or null for an unknown/destroyed object.</summary>
        public static Object InstanceIDToObjectCompat(int instanceId)
        {
#if UNITY_6000_6_OR_NEWER
            if (instanceId == 0) return null;
            if (!Entities.TryGetValue(instanceId, out ulong entityId))
            {
                string value = SessionState.GetString(Prefix + "handle." + instanceId, "");
                if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out entityId))
                    return null;
                Entities[instanceId] = entityId;
                Handles[entityId] = instanceId;
            }
            return EditorUtility.EntityIdToObject(EntityId.FromULong(entityId));
#elif UNITY_6000_3_OR_NEWER
            return EditorUtility.EntityIdToObject(instanceId);
#else
            return EditorUtility.InstanceIDToObject(instanceId);
#endif
        }
#endif
    }
}
