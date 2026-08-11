using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services.Transport;
using MCPForUnity.Editor.Services.Transport.Transports;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace MCPForUnityTests.Editor.Services
{
    [TestFixture]
    public class WebSocketTransportClientTests
    {
        private const string CandidateBuilderMethodName = "BuildConnectionCandidateUris";
        private const string WebSocketTransportClientTypeName = "MCPForUnity.Editor.Services.Transport.Transports.WebSocketTransportClient";
        private static readonly MethodInfo BuildConnectionCandidateUrisMethod = ResolveCandidateBuilderMethod();

        [Test]
        public void BuildConnectionCandidateUris_NullEndpoint_ReturnsEmptyList()
        {
            // Act
            List<Uri> candidates = InvokeBuildConnectionCandidateUris(null);

            // Assert
            Assert.IsNotNull(candidates);
            Assert.AreEqual(0, candidates.Count);
        }

        [Test]
        public void BuildConnectionCandidateUris_NonLocalhost_ReturnsOriginalOnly()
        {
            // Arrange
            var endpoint = new Uri("ws://127.0.0.1:8080/hub/plugin");

            // Act
            List<Uri> candidates = InvokeBuildConnectionCandidateUris(endpoint);

            // Assert
            Assert.AreEqual(1, candidates.Count);
            Assert.AreEqual(endpoint, candidates[0]);
        }

        [Test]
        public void BuildConnectionCandidateUris_Localhost_AddsIPv4AndIPv6Fallbacks()
        {
            // Arrange
            var endpoint = new Uri("ws://localhost:8080/hub/plugin");

            // Act
            List<Uri> candidates = InvokeBuildConnectionCandidateUris(endpoint);

            // Assert
            Assert.AreEqual(3, candidates.Count);
            CollectionAssert.AreEqual(
                new[] { "localhost", "127.0.0.1", "::1" },
                candidates.Select(uri => NormalizeHostForComparison(uri.Host)).ToArray());

            int uniqueCount = candidates
                .Select(uri => uri.AbsoluteUri)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            Assert.AreEqual(candidates.Count, uniqueCount, "Fallback list should not contain duplicate endpoints.");
        }

        [Test]
        public void BuildConnectionCandidateUris_LocalhostFallbacks_PreserveSchemePortPathAndQuery()
        {
            // Arrange
            var endpoint = new Uri("wss://localhost:9443/custom/path?mode=test");

            // Act
            List<Uri> candidates = InvokeBuildConnectionCandidateUris(endpoint);

            // Assert
            Assert.AreEqual(3, candidates.Count);
            foreach (Uri candidate in candidates)
            {
                Assert.AreEqual("wss", candidate.Scheme);
                Assert.AreEqual(9443, candidate.Port);
                Assert.AreEqual("/custom/path", candidate.AbsolutePath);
                Assert.AreEqual("?mode=test", candidate.Query);
            }
        }

        [Test]
        public async Task HandleSocketClosureAsync_CompletesOnlyItsOwnHandshakeAttempt()
        {
            var client = new WebSocketTransportClient();
            var closedAttempt = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var unrelatedAttempt = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            SetInstanceField(client, "_lifecycleCts", new CancellationTokenSource());

            try
            {
                await InvokePrivateTask(
                    client,
                    "HandleSocketClosureAsync",
                    "closed before registration",
                    closedAttempt);

                Assert.IsTrue(closedAttempt.Task.IsCompleted);
                Assert.IsFalse(await closedAttempt.Task);
                Assert.IsFalse(
                    unrelatedAttempt.Task.IsCompleted,
                    "A closure from an older socket must not complete a newer attempt's handshake.");
                Assert.AreEqual(0, GetInstanceField<int>(client, "_isReconnectingFlag"));
            }
            finally
            {
                await client.StopAsync();
            }
        }

        [Test]
        public async Task HandleSocketClosureAsync_DuringReconnect_ClearsConnectedState()
        {
            var client = new WebSocketTransportClient();
            var attempt = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            SetInstanceField(client, "_lifecycleCts", new CancellationTokenSource());
            SetInstanceField(client, "_isConnected", true);
            SetInstanceField(client, "_registrationAccepted", true);
            SetInstanceField(client, "_isReconnectingFlag", 1);

            try
            {
                await InvokePrivateTask(
                    client,
                    "HandleSocketClosureAsync",
                    "closed during reconnect registration",
                    attempt);

                Assert.IsFalse(client.IsConnected);
                Assert.IsFalse(client.State.IsConnected);
                Assert.IsFalse(GetInstanceField<bool>(client, "_registrationAccepted"));
                Assert.IsTrue(attempt.Task.IsCompleted);
                Assert.IsFalse(await attempt.Task);
            }
            finally
            {
                SetInstanceField(client, "_isReconnectingFlag", 0);
                await client.StopAsync();
            }
        }

        [Test]
        public async Task HandleMessageAsync_ExecuteBeforeHandshake_CancelsConnection()
        {
            var client = new WebSocketTransportClient();
            var lifecycleCts = new CancellationTokenSource();
            var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(
                lifecycleCts.Token);
            var attempt = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            SetInstanceField(client, "_lifecycleCts", lifecycleCts);
            SetInstanceField(client, "_connectionCts", connectionCts);
            const string error =
                "Python server sent a command before the mandatory bridge handshake completed.";

            LogAssert.Expect(LogType.Error, new Regex(Regex.Escape(error)));
            await InvokePrivateTask(
                client,
                "HandleMessageAsync",
                "{\"type\":\"execute\",\"id\":\"unsafe\",\"name\":\"manage_scene\"}",
                CancellationToken.None,
                attempt);

            Assert.IsTrue(connectionCts.IsCancellationRequested);
            Assert.IsFalse(client.IsConnected);
            Assert.AreEqual(error, client.State.Error);
            Assert.IsTrue(attempt.Task.IsCompleted);
            Assert.IsFalse(await attempt.Task);

            await client.StopAsync();
        }

        [Test]
        public async Task StopAsync_PreservesActionableHandshakeError()
        {
            var client = new WebSocketTransportClient();
            var attempt = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            SetInstanceField(client, "_lifecycleCts", new CancellationTokenSource());
            const string mismatch =
                "Server/package version mismatch: install matching releases and restart both sides.";

            LogAssert.Expect(LogType.Error, new Regex(Regex.Escape(mismatch)));
            InvokePrivate(client, "FailHandshake", mismatch, attempt);
            await client.StopAsync();

            Assert.IsFalse(client.State.IsConnected);
            Assert.AreEqual(mismatch, client.State.Error);
            Assert.IsTrue(attempt.Task.IsCompleted);
            Assert.IsFalse(await attempt.Task);
        }

        private static List<Uri> InvokeBuildConnectionCandidateUris(Uri endpoint)
        {
            if (BuildConnectionCandidateUrisMethod == null)
            {
                Assert.Fail(BuildMissingMethodDiagnostic());
            }
            var result = BuildConnectionCandidateUrisMethod.Invoke(null, new object[] { endpoint });
            Assert.IsNotNull(result);
            Assert.IsInstanceOf<List<Uri>>(result);
            return (List<Uri>)result;
        }

        private static void SetInstanceField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, $"Expected private field '{fieldName}' to exist.");
            field.SetValue(target, value);
        }

        private static T GetInstanceField<T>(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, $"Expected private field '{fieldName}' to exist.");
            return (T)field.GetValue(target);
        }

        private static object InvokePrivate(object target, string methodName, params object[] args)
        {
            MethodInfo method = target.GetType().GetMethod(
                methodName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(method, $"Expected private method '{methodName}' to exist.");
            return method.Invoke(target, args);
        }

        private static Task InvokePrivateTask(
            object target, string methodName, params object[] args)
        {
            return (Task)InvokePrivate(target, methodName, args);
        }

        private static MethodInfo ResolveCandidateBuilderMethod()
        {
            MethodInfo direct = GetCandidateBuilderMethod(typeof(WebSocketTransportClient));
            if (direct != null)
            {
                return direct;
            }

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type candidateType = assembly.GetType(WebSocketTransportClientTypeName);
                if (candidateType == null)
                {
                    continue;
                }

                MethodInfo method = GetCandidateBuilderMethod(candidateType);
                if (method != null)
                {
                    return method;
                }
            }

            return null;
        }

        private static MethodInfo GetCandidateBuilderMethod(Type type)
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
            MethodInfo direct = type.GetMethod(
                CandidateBuilderMethodName,
                flags,
                binder: null,
                types: new[] { typeof(Uri) },
                modifiers: null);
            if (direct != null)
            {
                return direct;
            }

            // Fallback for environments where signature binding can differ between loaded copies.
            return type.GetMethods(flags).FirstOrDefault(method =>
            {
                if (!string.Equals(method.Name, CandidateBuilderMethodName, StringComparison.Ordinal))
                {
                    return false;
                }

                ParameterInfo[] parameters = method.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType == typeof(Uri);
            });
        }

        private static string BuildMissingMethodDiagnostic()
        {
            var sb = new StringBuilder();
            sb.Append("Expected private candidate builder method to exist. Searched loaded assemblies for ")
              .Append(WebSocketTransportClientTypeName)
              .Append('.')
              .Append(CandidateBuilderMethodName)
              .Append(". Loaded candidate types:");

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type candidateType = assembly.GetType(WebSocketTransportClientTypeName);
                if (candidateType == null)
                {
                    continue;
                }

                sb.Append("\n- ")
                  .Append(assembly.FullName)
                  .Append(" @ ")
                  .Append(string.IsNullOrEmpty(assembly.Location) ? "<dynamic>" : assembly.Location);
            }

            return sb.ToString();
        }

        private static string NormalizeHostForComparison(string host)
        {
            if (string.IsNullOrEmpty(host))
            {
                return host;
            }

            return host.Trim('[', ']');
        }
    }
}
