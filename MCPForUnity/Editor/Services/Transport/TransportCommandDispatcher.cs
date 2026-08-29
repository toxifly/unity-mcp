using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Models;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditorInternal;

namespace MCPForUnity.Editor.Services.Transport
{
    /// <summary>
    /// Centralised command execution pipeline shared by all transport implementations.
    /// Guarantees that MCP commands are executed on the Unity main thread while preserving
    /// the legacy response format expected by the server.
    /// </summary>
    [InitializeOnLoad]
    internal static class TransportCommandDispatcher
    {
        private static SynchronizationContext _mainThreadContext;
        private static int _mainThreadId;
        private static int _processingFlag;

        private sealed class PendingCommand
        {
            public PendingCommand(
                string commandJson,
                TaskCompletionSource<string> completionSource,
                CancellationToken cancellationToken,
                CancellationTokenRegistration registration,
                long queuedAtTimestamp)
            {
                CommandJson = commandJson;
                CompletionSource = completionSource;
                CancellationToken = cancellationToken;
                CancellationRegistration = registration;
                QueuedAtTimestamp = queuedAtTimestamp;
            }

            public string CommandJson { get; }
            public TaskCompletionSource<string> CompletionSource { get; }
            public CancellationToken CancellationToken { get; }
            public CancellationTokenRegistration CancellationRegistration { get; }
            public bool IsExecuting { get; set; }
            public long QueuedAtTimestamp { get; }

            public void Dispose()
            {
                CancellationRegistration.Dispose();
            }

            public void TrySetResult(string payload)
            {
                CompletionSource.TrySetResult(payload);
            }

            public void TrySetCanceled()
            {
                CompletionSource.TrySetCanceled(CancellationToken);
            }
        }

        private static readonly Dictionary<string, PendingCommand> Pending = new();
        private static readonly object PendingLock = new();
        private static bool updateHooked;
        private static bool initialised;

        static TransportCommandDispatcher()
        {
            // Ensure this runs on the Unity main thread at editor load.
            _mainThreadContext = SynchronizationContext.Current;
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;

            EnsureInitialised();

            // Always keep the update hook installed so commands arriving from background
            // websocket tasks don't depend on a background-thread event subscription.
            if (!updateHooked)
            {
                updateHooked = true;
                EditorApplication.update += ProcessQueue;
            }
        }

        /// <summary>
        /// Schedule a command for execution on the Unity main thread and await its JSON response.
        /// </summary>
        public static Task<string> ExecuteCommandJsonAsync(string commandJson, CancellationToken cancellationToken)
        {
            return ExecuteCommandJsonAsync(
                commandJson,
                cancellationToken,
                Stopwatch.GetTimestamp());
        }

        /// <summary>
        /// Schedule a command that may already have waited in an upstream in-process queue.
        /// The monotonic enqueue timestamp keeps that first-hop wait in the dispatcher diagnostic.
        /// </summary>
        internal static Task<string> ExecuteCommandJsonAsync(
            string commandJson,
            CancellationToken cancellationToken,
            long queuedAtTimestamp)
        {
            if (commandJson is null)
            {
                throw new ArgumentNullException(nameof(commandJson));
            }

            EnsureInitialised();

            var id = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            var registration = cancellationToken.CanBeCanceled
                ? cancellationToken.Register(() => CancelPending(id, cancellationToken))
                : default;

            var pending = new PendingCommand(
                commandJson,
                tcs,
                cancellationToken,
                registration,
                queuedAtTimestamp);

            lock (PendingLock)
            {
                Pending[id] = pending;
            }

            // Proactively wake up the main thread execution loop. This improves responsiveness
            // in scenarios where EditorApplication.update is throttled or temporarily not firing
            // (e.g., Unity unfocused, compiling, or during domain reload transitions).
            RequestMainThreadPump();

            return tcs.Task;
        }

        internal static Task<T> RunOnMainThreadAsync<T>(Func<T> func, CancellationToken cancellationToken)
        {
            if (func is null)
            {
                throw new ArgumentNullException(nameof(func));
            }

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            var registration = cancellationToken.CanBeCanceled
                ? cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken))
                : default;

            void Invoke()
            {
                try
                {
                    if (tcs.Task.IsCompleted)
                    {
                        return;
                    }

                    var result = func();
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    registration.Dispose();
                }
            }

            // Best-effort nudge: if we're posting from a background thread (e.g., websocket receive),
            // encourage Unity to run a loop iteration so the posted callback can execute even when unfocused.
            try { EditorApplication.QueuePlayerLoopUpdate(); } catch { }

            if (_mainThreadContext != null && Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            {
                _mainThreadContext.Post(_ => Invoke(), null);
                return tcs.Task;
            }

            Invoke();
            return tcs.Task;
        }

        private static void RequestMainThreadPump()
        {
            void Pump()
            {
                try
                {
                    // Hint Unity to run a loop iteration soon.
                    EditorApplication.QueuePlayerLoopUpdate();
                }
                catch
                {
                    // Best-effort only.
                }

                ProcessQueue();
            }

            if (_mainThreadContext != null && Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            {
                _mainThreadContext.Post(_ => Pump(), null);
                return;
            }

            Pump();
        }

        private static void EnsureInitialised()
        {
            if (initialised)
            {
                return;
            }

            CommandRegistry.Initialize();
            initialised = true;
        }

        private static void HookUpdate()
        {
            // Deprecated: we keep the update hook installed permanently (see static ctor).
            if (updateHooked) return;
            updateHooked = true;
            EditorApplication.update += ProcessQueue;
        }

        private static void UnhookUpdateIfIdle()
        {
            // Intentionally no-op: keep update hook installed so background commands always process.
            // This avoids "must focus Unity to re-establish contact" edge cases.
            return;
        }

        private static void ProcessQueue()
        {
            if (Interlocked.Exchange(ref _processingFlag, 1) == 1)
            {
                return;
            }

            try
            {
            List<(string id, PendingCommand pending)> ready;

            lock (PendingLock)
            {
                // Early exit inside lock to prevent per-frame List allocations (GitHub issue #577)
                if (Pending.Count == 0)
                {
                    return;
                }

                ready = new List<(string, PendingCommand)>(Pending.Count);
                foreach (var kvp in Pending)
                {
                    if (kvp.Value.IsExecuting)
                    {
                        continue;
                    }

                    kvp.Value.IsExecuting = true;
                    ready.Add((kvp.Key, kvp.Value));
                }

                if (ready.Count == 0)
                {
                    UnhookUpdateIfIdle();
                    return;
                }
            }

            foreach (var (id, pending) in ready)
            {
                ProcessCommand(id, pending);
            }
            }
            finally
            {
                Interlocked.Exchange(ref _processingFlag, 0);
            }
        }

        /// <summary>
        /// How long a command sat in the queue before the main thread could take it, and what was
        /// holding the thread when it finally did. A read-only call can block for a minute behind
        /// a domain reload and look, from the caller's side, like the tool itself was slow.
        /// </summary>
        private const double QueueWaitReportThresholdMs = 2000d;

        internal static object DescribeQueueWait(DateTime queuedAtUtc, string commandType)
        {
            JObject queueWait = CaptureQueueWait(queuedAtUtc);
            LogQueueWait(queueWait, commandType);
            return queueWait;
        }

        internal static JObject CaptureQueueWait(DateTime queuedAtUtc)
        {
            return CaptureQueueWait(queuedAtUtc, DateTime.UtcNow, GetQueueWaitReason());
        }

        internal static JObject CaptureQueueWait(long queuedAtTimestamp)
        {
            return CaptureQueueWait(
                queuedAtTimestamp,
                Stopwatch.GetTimestamp(),
                GetQueueWaitReason());
        }

        internal static JObject CaptureQueueWait(
            long queuedAtTimestamp,
            long processingStartedAtTimestamp,
            string reason)
        {
            double waitedMs = ElapsedMilliseconds(
                queuedAtTimestamp,
                processingStartedAtTimestamp);
            return BuildQueueWait(waitedMs, reason);
        }

        internal static JObject CaptureQueueWait(
            DateTime queuedAtUtc,
            DateTime processingStartedAtUtc,
            string reason)
        {
            double waitedMs = (processingStartedAtUtc - queuedAtUtc).TotalMilliseconds;
            return BuildQueueWait(waitedMs, reason);
        }

        private static double ElapsedMilliseconds(long startedAtTimestamp, long endedAtTimestamp)
        {
            if (endedAtTimestamp <= startedAtTimestamp)
            {
                return 0d;
            }

            return (endedAtTimestamp - startedAtTimestamp) * 1000d / Stopwatch.Frequency;
        }

        private static JObject BuildQueueWait(double waitedMs, string reason)
        {
            if (waitedMs < QueueWaitReportThresholdMs)
            {
                return null;
            }

            return new JObject
            {
                ["waited_ms"] = (long)waitedMs,
                ["reason"] = reason,
            };
        }

        private static string GetQueueWaitReason()
        {
            return ClassifyQueueWaitReason(
                EditorStateCache.GetActualIsCompiling(),
                EditorApplication.isUpdating,
                EditorApplication.isPlayingOrWillChangePlaymode,
                InternalEditorUtility.isApplicationActive);
        }

        internal static string ClassifyQueueWaitReason(
            bool isActuallyCompiling,
            bool isUpdating,
            bool isPlayingOrWillChangePlaymode,
            bool isApplicationActive)
        {
            return isActuallyCompiling
                ? "compiling"
                : isUpdating
                    ? "asset_import"
                    : isPlayingOrWillChangePlaymode
                        ? "play_mode_transition"
                        : !isApplicationActive
                            ? "editor_unfocused"
                            : "main_thread_busy";
        }

        private static void LogQueueWait(JObject queueWait, string commandType)
        {
            if (queueWait == null)
            {
                return;
            }

            long waitedMs = queueWait.Value<long>("waited_ms");
            string reason = queueWait.Value<string>("reason");
            McpLog.Warn(
                $"[Dispatcher] '{commandType}' waited {waitedMs}ms for the main thread ({reason}).");
        }

        private static void ProcessCommand(string id, PendingCommand pending)
        {
            // Snapshot both elapsed time and editor state before parsing or executing the command.
            // Tool duration and side effects must not be reported as time spent in this queue.
            JObject queueWait = CaptureQueueWait(pending.QueuedAtTimestamp);

            if (pending.CancellationToken.IsCancellationRequested)
            {
                RemovePending(id, pending);
                pending.TrySetCanceled();
                return;
            }

            string commandText = pending.CommandJson?.Trim();
            if (string.IsNullOrEmpty(commandText))
            {
                pending.TrySetResult(SerializeError("Empty command received"));
                RemovePending(id, pending);
                return;
            }

            if (string.Equals(commandText, "ping", StringComparison.OrdinalIgnoreCase))
            {
                var pingResponse = new
                {
                    status = "success",
                    result = new { message = "pong" }
                };
                pending.TrySetResult(JsonConvert.SerializeObject(pingResponse));
                RemovePending(id, pending);
                return;
            }

            if (!IsValidJson(commandText))
            {
                var invalidJsonResponse = new
                {
                    status = "error",
                    error = "Invalid JSON format",
                    receivedText = commandText.Length > 50 ? commandText[..50] + "..." : commandText
                };
                pending.TrySetResult(JsonConvert.SerializeObject(invalidJsonResponse));
                RemovePending(id, pending);
                return;
            }

            try
            {
                var command = JsonConvert.DeserializeObject<Command>(commandText);
                if (command == null)
                {
                    pending.TrySetResult(SerializeError("Command deserialized to null", "Unknown", commandText));
                    RemovePending(id, pending);
                    return;
                }

                if (string.IsNullOrWhiteSpace(command.type))
                {
                    pending.TrySetResult(SerializeError("Command type cannot be empty"));
                    RemovePending(id, pending);
                    return;
                }

                if (string.Equals(command.type, "ping", StringComparison.OrdinalIgnoreCase))
                {
                    var pingResponse = new
                    {
                        status = "success",
                        result = new { message = "pong" }
                    };
                    pending.TrySetResult(JsonConvert.SerializeObject(pingResponse));
                    RemovePending(id, pending);
                    return;
                }

                var parameters = command.@params ?? new JObject();

                // Block execution of disabled resources
                var resourceMeta = MCPServiceLocator.ResourceDiscovery.GetResourceMetadata(command.type);
                if (resourceMeta != null && !MCPServiceLocator.ResourceDiscovery.IsResourceEnabled(command.type))
                {
                    pending.TrySetResult(SerializeError(
                        $"Resource '{command.type}' is disabled in the Unity Editor."));
                    RemovePending(id, pending);
                    return;
                }

                // Block execution of explicitly disabled tools. Default-off group
                // tools stay executable so per-session activation works.
                var toolMeta = MCPServiceLocator.ToolDiscovery.GetToolMetadata(command.type);
                if (toolMeta != null && MCPServiceLocator.ToolDiscovery.IsToolExplicitlyDisabled(command.type))
                {
                    pending.TrySetResult(SerializeError(
                        $"Tool '{command.type}' is disabled in the Unity Editor."));
                    RemovePending(id, pending);
                    return;
                }

                var logType = resourceMeta != null ? "resource" : toolMeta != null ? "tool" : "unknown";
                var sw = McpLogRecord.IsEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;
                LogQueueWait(queueWait, command.type);
                var result = CommandRegistry.ExecuteCommand(
                    command.type,
                    parameters,
                    pending.CompletionSource,
                    queueWait);

                if (result == null)
                {
                    // Async command – cleanup after completion on next editor frame to preserve order.
                    var capturedType = command.type;
                    var capturedParams = parameters;
                    var capturedLogType = logType;
                    pending.CompletionSource.Task.ContinueWith(t =>
                    {
                        sw?.Stop();
                        var logStatus = "SUCCESS";
                        string logError = null;
                        if (t.IsFaulted)
                        {
                            logStatus = "ERROR";
                            logError = t.Exception?.InnerException?.Message;
                        }
                        else if (t.IsCompletedSuccessfully && t.Result != null)
                        {
                            try
                            {
                                var resultObj = JObject.Parse(t.Result);
                                if (string.Equals(resultObj.Value<string>("status"), "error", StringComparison.OrdinalIgnoreCase))
                                {
                                    logStatus = "ERROR";
                                    logError = resultObj.Value<string>("error");
                                }
                            }
                            catch { }
                        }
                        McpLogRecord.Log(capturedType, capturedParams, capturedLogType,
                            logStatus, sw?.ElapsedMilliseconds ?? 0, logError);
                        EditorApplication.delayCall += () => RemovePending(id, pending);
                    }, TaskScheduler.Default);
                    return;
                }

                sw?.Stop();

                string syncLogStatus = "SUCCESS";
                string syncLogError = null;
                if (result is ErrorResponse errResp)
                {
                    syncLogStatus = "ERROR";
                    syncLogError = errResp.Error;
                }
                McpLogRecord.Log(command.type, parameters, logType, syncLogStatus, sw?.ElapsedMilliseconds ?? 0, syncLogError);

                var response = new
                {
                    status = "success",
                    result,
                    queue = queueWait,
                };
                pending.TrySetResult(JsonConvert.SerializeObject(response));
                RemovePending(id, pending);
            }
            catch (Exception ex)
            {
                McpLog.Error($"Error processing command: {ex.Message}\n{ex.StackTrace}");
                pending.TrySetResult(SerializeError(ex.Message, "Unknown (error during processing)", ex.StackTrace));
                RemovePending(id, pending);
            }
        }

        private static void CancelPending(string id, CancellationToken token)
        {
            PendingCommand pending = null;
            lock (PendingLock)
            {
                if (Pending.Remove(id, out pending))
                {
                    UnhookUpdateIfIdle();
                }
            }

            pending?.TrySetCanceled();
            pending?.Dispose();
        }

        private static void RemovePending(string id, PendingCommand pending)
        {
            lock (PendingLock)
            {
                Pending.Remove(id);
                UnhookUpdateIfIdle();
            }

            pending.Dispose();
        }

        private static string SerializeError(string message, string commandType = null, string stackTrace = null)
        {
            var errorResponse = new
            {
                status = "error",
                error = message,
                command = commandType ?? "Unknown",
                stackTrace
            };
            return JsonConvert.SerializeObject(errorResponse);
        }

        private static bool IsValidJson(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            text = text.Trim();
            if ((text.StartsWith("{") && text.EndsWith("}")) || (text.StartsWith("[") && text.EndsWith("]")))
            {
                try
                {
                    JToken.Parse(text);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }
    }
}
