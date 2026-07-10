using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Runtime.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>Bounded, opt-in lifecycle and serialized-property trace sessions.</summary>
    [InitializeOnLoad]
    [McpForUnityTool("lifecycle_trace", AutoRegister = true, Group = "core")]
    public static class LifecycleTrace
    {
        private const string ReloadStateKey = "MCPForUnity.LifecycleTrace.ReloadState.v1";
        private const int MaxTargets = 50;
        private const int MaxProperties = 100;
        private const int MaxEvents = 5000;
        private static readonly Dictionary<string, TraceSession> Sessions = new();
        private static readonly Dictionary<int, string> PendingProbes = new();
        private static readonly MethodInfo ClearSceneDirtiness = typeof(EditorSceneManager).GetMethod(
            "ClearSceneDirtiness",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(Scene) },
            null);
        private static bool _cleaningUp;

        static LifecycleTrace()
        {
            RestoreReloadedSessions();
            EditorApplication.update += Update;
            Selection.selectionChanged += SelectionChanged;
            EditorApplication.playModeStateChanged += PlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeAssemblyReload;
        }

        public static object HandleCommand(JObject @params)
        {
            if (@params == null)
                return Error("INVALID_PARAMS", "Parameters cannot be null.");

            string action = (@params.Value<string>("action") ?? string.Empty).Trim().ToLowerInvariant();
            try
            {
                switch (action)
                {
                    case "start": return Start(@params);
                    case "poll": return Poll(@params, false);
                    case "status": return Poll(@params, true);
                    case "stop": return Stop(@params);
                    default: return Error("INVALID_ACTION", "Action must be start, poll, status, or stop.");
                }
            }
            catch (Exception ex)
            {
                return Error("TRACE_FAILED", ex.Message);
            }
        }

        private static object Start(JObject p)
        {
            string[] requestedTargets = p["targets"]?.Values<string>().ToArray() ?? Array.Empty<string>();
            string[] events = p["events"]?.Values<string>().ToArray()
                ?? new[] { "Awake", "OnEnable", "OnDisable", "OnDestroy" };
            string[] properties = p["propertyWhitelist"]?.Values<string>().ToArray() ?? Array.Empty<string>();
            if (requestedTargets.Length == 0 || requestedTargets.Any(string.IsNullOrWhiteSpace))
                return Error("INVALID_PARAMS", "Start requires at least one non-empty target.");
            if (requestedTargets.Length > MaxTargets)
                return Error("PAYLOAD_LIMIT_EXCEEDED", $"At most {MaxTargets} targets may be traced.");
            if (properties.Length > MaxProperties)
                return Error("PAYLOAD_LIMIT_EXCEEDED", $"At most {MaxProperties} properties may be traced.");

            var allowedEvents = new HashSet<string>(new[]
            {
                "Awake", "OnEnable", "OnDisable", "OnDestroy",
                "serialized_property_change", "selection_change"
            }, StringComparer.Ordinal);
            if (events.Length == 0 || events.Any(item => !allowedEvents.Contains(item)))
                return Error("INVALID_EVENT", "One or more requested event names are unsupported.");

            var targets = new List<GameObject>();
            foreach (string requested in requestedTargets)
            {
                if (!TryResolveTarget(requested, out GameObject target, out string code, out string message))
                    return Error(code, message, new { target = requested });
                if (!targets.Contains(target)) targets.Add(target);
            }

            var session = new TraceSession
            {
                Id = Guid.NewGuid().ToString("N"),
                Status = "running",
                StartedUtc = DateTime.UtcNow,
                ExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Min(3600, Math.Max(1, p.Value<int?>("timeoutSeconds") ?? 300))),
                MaxEvents = Math.Min(MaxEvents, Math.Max(1, p.Value<int?>("maxEvents") ?? 500)),
                EventKinds = new HashSet<string>(events, StringComparer.Ordinal),
                PropertyWhitelist = properties,
                Targets = targets,
                TargetIds = new HashSet<int>(targets.Select(UnityObjectIdCompat.GetInstanceIDCompat)),
            };
            Sessions[session.Id] = session;

            try
            {
                PreserveSceneDirtyState(targets, () =>
                {
                    foreach (GameObject target in targets)
                    {
                        int id = UnityObjectIdCompat.GetInstanceIDCompat(target);
                        PendingProbes[id] = session.Id;
                        var probe = target.AddComponent<LifecycleTraceProbe>();
                        probe.hideFlags = HideFlags.HideInInspector | HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;
                        probe.Configure(session.Id);
                        session.Probes.Add(probe);
                        PendingProbes.Remove(id);
                    }
                });
            }
            catch
            {
                foreach (GameObject target in targets)
                    PendingProbes.Remove(UnityObjectIdCompat.GetInstanceIDCompat(target));
                Cleanup(session, "start_failed", true);
                Sessions.Remove(session.Id);
                throw;
            }

            SampleProperties(session, false);
            return new SuccessResponse("Lifecycle trace started.", SessionSummary(session, includeEvents: false));
        }

        private static object Poll(JObject p, bool statusOnly)
        {
            if (!TryGetSession(p, out TraceSession session, out object error)) return error;
            if (session.Status == "running") SampleProperties(session, true);
            int cursor = Math.Max(0, p.Value<int?>("cursor") ?? 0);
            int limit = Math.Min(500, Math.Max(1, p.Value<int?>("limit") ?? 100));
            object data = statusOnly
                ? SessionSummary(session, includeEvents: false)
                : SessionPage(session, cursor, limit);
            return new SuccessResponse(statusOnly ? "Lifecycle trace status." : "Lifecycle trace events.", data);
        }

        private static object Stop(JObject p)
        {
            if (!TryGetSession(p, out TraceSession session, out object error)) return error;
            if (session.Status == "running")
            {
                SampleProperties(session, true);
                Cleanup(session, "stopped", true);
            }
            int cursor = Math.Max(0, p.Value<int?>("cursor") ?? 0);
            int limit = Math.Min(500, Math.Max(1, p.Value<int?>("limit") ?? 100));
            return new SuccessResponse("Lifecycle trace stopped and instrumentation removed.", SessionPage(session, cursor, limit));
        }

        private static bool TryGetSession(JObject p, out TraceSession session, out object error)
        {
            string id = (p.Value<string>("sessionId") ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(id))
            {
                session = null;
                error = Error("INVALID_PARAMS", "A non-empty sessionId is required.");
                return false;
            }
            if (!Sessions.TryGetValue(id, out session))
            {
                error = Error("TRACE_SESSION_NOT_FOUND", $"Trace session '{id}' was not found.");
                return false;
            }
            error = null;
            return true;
        }

        internal static void ProbeEvent(LifecycleTraceProbe probe, string eventName)
        {
            if (_cleaningUp) return;
            string sessionId = probe != null ? probe.SessionId : null;
            GameObject target = probe != null ? probe.gameObject : null;
            if (string.IsNullOrEmpty(sessionId) && target != null)
                PendingProbes.TryGetValue(UnityObjectIdCompat.GetInstanceIDCompat(target), out sessionId);
            if (string.IsNullOrEmpty(sessionId) || !Sessions.TryGetValue(sessionId, out TraceSession session)) return;
            Record(session, target, eventName, null);
        }

        private static void Update()
        {
            foreach (TraceSession session in Sessions.Values.ToArray())
            {
                if (session.Status != "running") continue;
                if (DateTime.UtcNow >= session.ExpiresUtc)
                {
                    Cleanup(session, "timed_out", true);
                    continue;
                }
                SampleProperties(session, true);
            }
        }

        private static void SelectionChanged()
        {
            GameObject selected = Selection.activeGameObject;
            foreach (TraceSession session in Sessions.Values.Where(item => item.Status == "running"))
            {
                if (!session.EventKinds.Contains("selection_change")) continue;
                Record(session, selected, "selection_change", new[]
                {
                    new PropertyChange { Property = "Selection.activeGameObject", Before = null, After = selected != null ? Identity(selected) : null }
                });
            }
        }

        private static void SampleProperties(TraceSession session, bool emitChanges)
        {
            if (!session.EventKinds.Contains("serialized_property_change") || session.PropertyWhitelist.Length == 0) return;
            foreach (GameObject target in session.Targets.Where(item => item != null))
            {
                var changes = new List<PropertyChange>();
                foreach (string requested in session.PropertyWhitelist)
                {
                    if (!TryResolveProperty(target, requested, out UnityEngine.Object owner, out SerializedProperty property))
                    {
                        if (!session.Warnings.Contains(requested)) session.Warnings.Add(requested);
                        continue;
                    }
                    string key = UnityObjectIdCompat.GetInstanceIDCompat(target) + "|" + requested;
                    string value = SerializedValue(property);
                    if (session.PropertyValues.TryGetValue(key, out string before) && before != value && emitChanges)
                        changes.Add(new PropertyChange { Property = requested, Before = before, After = value });
                    session.PropertyValues[key] = value;
                }
                if (changes.Count > 0) Record(session, target, "serialized_property_change", changes);
            }
        }

        private static bool TryResolveProperty(GameObject target, string requested, out UnityEngine.Object owner, out SerializedProperty property)
        {
            owner = null;
            property = null;
            if (target == null || string.IsNullOrWhiteSpace(requested)) return false;
            var candidates = new List<UnityEngine.Object> { target };
            candidates.AddRange(target.GetComponents<Component>().Where(item => item != null).Cast<UnityEngine.Object>());
            foreach (UnityEngine.Object candidate in candidates.OrderByDescending(item => item.GetType().FullName.Length))
            {
                string[] names = { candidate.GetType().FullName, candidate.GetType().Name };
                string prefix = names.FirstOrDefault(name => requested.StartsWith(name + ".", StringComparison.Ordinal));
                if (prefix == null) continue;
                var serialized = new SerializedObject(candidate);
                SerializedProperty found = serialized.FindProperty(requested.Substring(prefix.Length + 1));
                if (found == null) continue;
                owner = candidate;
                property = found.Copy();
                return true;
            }
            return false;
        }

        private static string SerializedValue(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer: return property.longValue.ToString();
                case SerializedPropertyType.Boolean: return property.boolValue ? "true" : "false";
                case SerializedPropertyType.Float: return property.doubleValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                case SerializedPropertyType.String: return property.stringValue;
                case SerializedPropertyType.Color: return property.colorValue.ToString("R");
                case SerializedPropertyType.Enum: return property.enumValueIndex.ToString();
                case SerializedPropertyType.ObjectReference:
                    if (property.objectReferenceValue == null) return MissingReferenceValue(property);
                    return GlobalId(property.objectReferenceValue) ?? Identity(property.objectReferenceValue);
                case SerializedPropertyType.LayerMask: return property.intValue.ToString();
                case SerializedPropertyType.Vector2: return property.vector2Value.ToString("R");
                case SerializedPropertyType.Vector3: return property.vector3Value.ToString("R");
                case SerializedPropertyType.Vector4: return property.vector4Value.ToString("R");
                case SerializedPropertyType.Rect: return property.rectValue.ToString();
                case SerializedPropertyType.ArraySize: return property.intValue.ToString();
                case SerializedPropertyType.Character: return property.intValue.ToString();
                case SerializedPropertyType.AnimationCurve: return JsonConvert.SerializeObject(property.animationCurveValue);
                case SerializedPropertyType.Bounds: return property.boundsValue.ToString();
                case SerializedPropertyType.Quaternion: return property.quaternionValue.ToString("R");
                case SerializedPropertyType.Vector2Int: return property.vector2IntValue.ToString();
                case SerializedPropertyType.Vector3Int: return property.vector3IntValue.ToString();
                case SerializedPropertyType.RectInt: return property.rectIntValue.ToString();
                case SerializedPropertyType.BoundsInt: return property.boundsIntValue.ToString();
                case SerializedPropertyType.ManagedReference: return property.managedReferenceFullTypename ?? "null";
                default: return property.type ?? property.propertyType.ToString();
            }
        }

        private static string MissingReferenceValue(SerializedProperty property)
        {
#if UNITY_6000_5_OR_NEWER
            ulong entityId = EntityId.ToULong(property.objectReferenceEntityIdValue);
            return entityId == 0 ? "null" : $"missing:{entityId}";
#else
            return property.objectReferenceInstanceIDValue == 0
                ? "null"
                : $"missing:{property.objectReferenceInstanceIDValue}";
#endif
        }

        private static void Record(TraceSession session, GameObject target, string eventName, IEnumerable<PropertyChange> changes)
        {
            if (session == null || !session.EventKinds.Contains(eventName) || session.Truncated) return;
            if (session.Events.Count >= session.MaxEvents)
            {
                session.Truncated = true;
                return;
            }
            session.Sequence++;
            session.Events.Add(new TraceEvent
            {
                Sequence = session.Sequence,
                Frame = Time.frameCount,
                TimestampSeconds = EditorApplication.timeSinceStartup,
                Object = target != null ? GameObjectLookup.GetGameObjectPath(target) : null,
                GlobalObjectId = target != null ? GlobalId(target) : null,
                Event = eventName,
                Observation = "observed",
                CallbackSource = eventName == "Awake" || eventName.StartsWith("On", StringComparison.Ordinal)
                    ? "temporary_proxy"
                    : "editor_hook",
                Changes = changes?.ToList()
            });
        }

        private static void Cleanup(TraceSession session, string reason, bool destroyProbes)
        {
            if (session == null || session.Status != "running") return;
            _cleaningUp = true;
            try
            {
                if (destroyProbes)
                {
                    LifecycleTraceProbe[] probes = session.Probes.Where(item => item != null).ToArray();
                    PreserveSceneDirtyState(probes.Select(item => item.gameObject), () =>
                    {
                        foreach (LifecycleTraceProbe probe in probes)
                        {
                            if (EditorApplication.isPlaying) UnityEngine.Object.Destroy(probe);
                            else UnityEngine.Object.DestroyImmediate(probe);
                        }
                    });
                }
            }
            finally { _cleaningUp = false; }
            session.Probes.Clear();
            session.Status = reason == "timed_out" ? "timed_out" : "stopped";
            session.StopReason = reason;
            session.StoppedUtc = DateTime.UtcNow;
        }

        private static void PlayModeChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.ExitingPlayMode) return;
            foreach (TraceSession session in Sessions.Values.Where(item => item.Status == "running").ToArray())
                Cleanup(session, "play_mode_exit", true);
        }

        private static void BeforeAssemblyReload()
        {
            foreach (TraceSession session in Sessions.Values.Where(item => item.Status == "running").ToArray())
                Cleanup(session, "domain_reload", true);
            try { SessionState.SetString(ReloadStateKey, JsonConvert.SerializeObject(Sessions.Values)); }
            catch { SessionState.EraseString(ReloadStateKey); }
        }

        private static void RestoreReloadedSessions()
        {
            string json = SessionState.GetString(ReloadStateKey, null);
            SessionState.EraseString(ReloadStateKey);
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                foreach (TraceSession session in JsonConvert.DeserializeObject<List<TraceSession>>(json) ?? new List<TraceSession>())
                {
                    session.Targets = new List<GameObject>();
                    session.Probes = new List<LifecycleTraceProbe>();
                    Sessions[session.Id] = session;
                }
            }
            catch { }
        }

        private static void PreserveSceneDirtyState(IEnumerable<GameObject> objects, Action action)
        {
            var states = objects
                .Where(item => item != null && item.scene.IsValid() && item.scene.isLoaded)
                .Select(item => item.scene)
                .GroupBy(item => item.handle)
                .Select(group => new KeyValuePair<Scene, bool>(group.First(), group.First().isDirty))
                .ToArray();
            action();
            foreach (KeyValuePair<Scene, bool> state in states)
            {
                if (!state.Key.IsValid() || !state.Key.isLoaded) continue;
                if (state.Value)
                {
                    EditorSceneManager.MarkSceneDirty(state.Key);
                }
                else if (state.Key.isDirty)
                {
                    try
                    {
                        if (ClearSceneDirtiness != null)
                            ClearSceneDirtiness.Invoke(null, new object[] { state.Key });
                    }
                    catch { }
                    foreach (GameObject root in state.Key.GetRootGameObjects())
                    {
                        EditorUtility.ClearDirty(root);
                        foreach (Component component in root.GetComponentsInChildren<Component>(true).Where(item => item != null))
                            EditorUtility.ClearDirty(component);
                    }
                }
            }
        }

        private static bool TryResolveTarget(string requested, out GameObject target, out string code, out string message)
        {
            target = null;
            code = null;
            message = null;
            if (GlobalObjectId.TryParse(requested, out GlobalObjectId globalId))
            {
                target = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(globalId) as GameObject;
                if (target != null) return true;
            }
            string method = requested.Contains("/") ? "by_path" : "by_name";
            List<int> matches = GameObjectLookup.SearchGameObjects(method, requested, true, 2);
            if (matches.Count == 1)
            {
                target = GameObjectLookup.FindById(matches[0]);
                return target != null;
            }
            code = matches.Count > 1 ? "TARGET_AMBIGUOUS" : "TARGET_NOT_FOUND";
            message = matches.Count > 1
                ? $"Target '{requested}' matched multiple scene objects; use a hierarchy path or GlobalObjectId."
                : $"Target '{requested}' was not found.";
            return false;
        }

        private static object SessionSummary(TraceSession session, bool includeEvents)
        {
            return new
            {
                session_id = session.Id,
                status = session.Status,
                stop_reason = session.StopReason,
                started_utc = session.StartedUtc,
                stopped_utc = session.StoppedUtc,
                expires_utc = session.ExpiresUtc,
                event_count = session.Events.Count,
                max_events = session.MaxEvents,
                truncated = session.Truncated,
                next_cursor = session.Events.Count,
                unresolved_properties = session.Warnings.ToArray(),
                instrumentation_attached = session.Status == "running" && session.Probes.Any(item => item != null),
                events = includeEvents ? session.Events : null
            };
        }

        private static object SessionPage(TraceSession session, int cursor, int limit)
        {
            TraceEvent[] page = session.Events.Skip(cursor).Take(limit).ToArray();
            int next = Math.Min(session.Events.Count, cursor + page.Length);
            return new
            {
                session_id = session.Id,
                status = session.Status,
                stop_reason = session.StopReason,
                events = page,
                event_count = session.Events.Count,
                next_cursor = next,
                has_more = next < session.Events.Count,
                truncated = session.Truncated,
                unresolved_properties = session.Warnings.ToArray(),
                instrumentation_attached = session.Status == "running" && session.Probes.Any(item => item != null)
            };
        }

        private static string GlobalId(UnityEngine.Object target)
        {
            if (target == null) return null;
            try
            {
                GlobalObjectId id = GlobalObjectId.GetGlobalObjectIdSlow(target);
                string value = id.ToString();
                return string.IsNullOrEmpty(value) || value.EndsWith("-0-0", StringComparison.Ordinal) ? null : value;
            }
            catch { return null; }
        }

        private static string Identity(UnityEngine.Object target)
        {
            if (target == null) return null;
            GameObject gameObject = target as GameObject ?? (target as Component)?.gameObject;
            return gameObject != null ? GameObjectLookup.GetGameObjectPath(gameObject) : target.name;
        }

        private static ErrorResponse Error(string code, string message, object data = null)
            => new ErrorResponse(code, new { message, details = data });

        private sealed class TraceSession
        {
            public string Id;
            public string Status;
            public string StopReason;
            public DateTime StartedUtc;
            public DateTime? StoppedUtc;
            public DateTime ExpiresUtc;
            public int MaxEvents;
            public long Sequence;
            public bool Truncated;
            public HashSet<string> EventKinds = new(StringComparer.Ordinal);
            public string[] PropertyWhitelist = Array.Empty<string>();
            public List<TraceEvent> Events = new();
            public List<string> Warnings = new();
            public Dictionary<string, string> PropertyValues = new();
            [JsonIgnore] public List<GameObject> Targets = new();
            [JsonIgnore] public HashSet<int> TargetIds = new();
            [JsonIgnore] public List<LifecycleTraceProbe> Probes = new();
        }

        private sealed class TraceEvent
        {
            [JsonProperty("sequence")] public long Sequence;
            [JsonProperty("frame")] public int Frame;
            [JsonProperty("timestamp_seconds")] public double TimestampSeconds;
            [JsonProperty("object")] public string Object;
            [JsonProperty("global_object_id")] public string GlobalObjectId;
            [JsonProperty("event")] public string Event;
            [JsonProperty("observation")] public string Observation;
            [JsonProperty("callback_source")] public string CallbackSource;
            [JsonProperty("changes", NullValueHandling = NullValueHandling.Ignore)] public List<PropertyChange> Changes;
        }

        private sealed class PropertyChange
        {
            [JsonProperty("property")] public string Property;
            [JsonProperty("before")] public string Before;
            [JsonProperty("after")] public string After;
        }
    }

}
