using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;

namespace MCPForUnity.Editor.Services
{
    /// <summary>Persists terminal compilation counters across assembly reloads.</summary>
    [InitializeOnLoad]
    internal static class CompilationStateTracker
    {
        private const string Prefix = "MCPForUnity.Compilation.";
        private const string ActiveKey = Prefix + "Active";
        private const string StartedKey = Prefix + "StartedUnixMs";
        private const string FinishedKey = Prefix + "FinishedUnixMs";
        private const string ErrorsKey = Prefix + "Errors";
        private const string WarningsKey = Prefix + "Warnings";
        private const string DurationKey = Prefix + "DurationSeconds";
        private const string ErrorDetailsKey = Prefix + "ErrorDetails";

        /// <summary>How many compiler errors are kept verbatim so callers can act without a console read.</summary>
        private const int MaxCapturedErrors = 10;
        private const int MaxCapturedMessageChars = 400;

        static CompilationStateTracker()
        {
            CompilationPipeline.compilationStarted -= OnCompilationStarted;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompilationFinished;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
        }

        internal static int LastErrors => SessionState.GetInt(ErrorsKey, 0);
        internal static int LastWarnings => SessionState.GetInt(WarningsKey, 0);
        internal static long? LastStartedUnixMs => ReadLong(StartedKey);
        internal static long? LastFinishedUnixMs => ReadLong(FinishedKey);
        /// <summary>The first <see cref="MaxCapturedErrors"/> errors of the last compilation, oldest first.</summary>
        internal static JArray LastErrorDetails
        {
            get
            {
                string raw = SessionState.GetString(ErrorDetailsKey, string.Empty);
                if (string.IsNullOrEmpty(raw)) return null;
                try
                {
                    var parsed = JArray.Parse(raw);
                    return parsed.Count > 0 ? parsed : null;
                }
                catch (JsonException)
                {
                    return null;
                }
            }
        }

        internal static double LastDurationSeconds
        {
            get
            {
                return double.TryParse(
                    SessionState.GetString(DurationKey, "0"),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var value) ? value : 0;
            }
        }

        private static void OnCompilationStarted(object _)
        {
            SessionState.SetBool(ActiveKey, true);
            SessionState.SetString(StartedKey, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
            SessionState.SetInt(ErrorsKey, 0);
            SessionState.SetInt(WarningsKey, 0);
            SessionState.SetString(DurationKey, "0");
            SessionState.SetString(ErrorDetailsKey, string.Empty);
        }

        private static void OnAssemblyCompilationFinished(string _, CompilerMessage[] messages)
        {
            var errors = SessionState.GetInt(ErrorsKey, 0);
            var warnings = SessionState.GetInt(WarningsKey, 0);
            var details = LastErrorDetails ?? new JArray();
            bool detailsChanged = false;
            foreach (var message in messages ?? Array.Empty<CompilerMessage>())
            {
                if (message.type == CompilerMessageType.Error)
                {
                    errors++;
                    if (details.Count < MaxCapturedErrors)
                    {
                        details.Add(Describe(message));
                        detailsChanged = true;
                    }
                }
                else if (message.type == CompilerMessageType.Warning) warnings++;
            }
            SessionState.SetInt(ErrorsKey, errors);
            SessionState.SetInt(WarningsKey, warnings);
            if (detailsChanged)
            {
                SessionState.SetString(ErrorDetailsKey, details.ToString(Formatting.None));
            }
        }

        /// <summary>
        /// Flattens a compiler error into the shape the server surfaces inline. Unity prefixes
        /// CompilerMessage.message with "file(line,col): "; the structured fields already carry
        /// that, so a prefix that literally starts with the message's own file is stripped.
        /// </summary>
        internal static JObject Describe(CompilerMessage message)
        {
            string text = message.message ?? string.Empty;
            if (!string.IsNullOrEmpty(message.file) && text.StartsWith(message.file, StringComparison.Ordinal))
            {
                int afterLocation = text.IndexOf("): ", message.file.Length, StringComparison.Ordinal);
                if (afterLocation >= 0)
                {
                    text = text.Substring(afterLocation + 3);
                }
            }
            text = text.Trim();
            if (text.Length > MaxCapturedMessageChars)
            {
                text = text.Substring(0, MaxCapturedMessageChars) + "...";
            }

            return new JObject
            {
                ["file"] = message.file,
                ["line"] = message.line,
                ["column"] = message.column,
                ["message"] = text
            };
        }

        private static void OnCompilationFinished(object _)
        {
            SessionState.SetString(FinishedKey, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
            if (long.TryParse(SessionState.GetString(StartedKey, "0"), out var started) && started > 0)
            {
                var duration = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - started) / 1000.0;
                SessionState.SetString(DurationKey, duration.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            SessionState.SetBool(ActiveKey, false);
        }

        private static long? ReadLong(string key)
        {
            return long.TryParse(SessionState.GetString(key, string.Empty), out var value) ? value : null;
        }
    }
}
