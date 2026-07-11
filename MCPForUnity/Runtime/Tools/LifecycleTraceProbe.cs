using System;
using UnityEngine;

namespace MCPForUnity.Runtime.Tools
{
    /// <summary>
    /// Temporary, non-persistent callback proxy owned by a lifecycle trace session.
    /// Lives in the runtime assembly because AddComponent rejects editor scripts;
    /// inert outside the Editor (the sink is only wired by the Editor assembly).
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("")]
    public sealed class LifecycleTraceProbe : MonoBehaviour
    {
        internal static Action<LifecycleTraceProbe, string> EventSink;

        [NonSerialized] private string _sessionId;
        internal string SessionId => _sessionId;
        internal void Configure(string sessionId) => _sessionId = sessionId;
        private void Awake() => EventSink?.Invoke(this, "Awake");
        private void OnEnable() => EventSink?.Invoke(this, "OnEnable");
        private void OnDisable() => EventSink?.Invoke(this, "OnDisable");
        private void OnDestroy() => EventSink?.Invoke(this, "OnDestroy");
    }
}
