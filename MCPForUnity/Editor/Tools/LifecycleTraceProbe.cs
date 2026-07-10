using System;
using UnityEngine;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>Temporary, non-persistent callback proxy owned by a trace session.</summary>
    [ExecuteAlways]
    [AddComponentMenu("")]
    public sealed class LifecycleTraceProbe : MonoBehaviour
    {
        [NonSerialized] private string _sessionId;
        internal string SessionId => _sessionId;
        internal void Configure(string sessionId) => _sessionId = sessionId;
        private void Awake() => LifecycleTrace.ProbeEvent(this, "Awake");
        private void OnEnable() => LifecycleTrace.ProbeEvent(this, "OnEnable");
        private void OnDisable() => LifecycleTrace.ProbeEvent(this, "OnDisable");
        private void OnDestroy() => LifecycleTrace.ProbeEvent(this, "OnDestroy");
    }
}
