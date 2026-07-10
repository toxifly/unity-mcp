using System;
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
        }

        private static void OnAssemblyCompilationFinished(string _, CompilerMessage[] messages)
        {
            var errors = SessionState.GetInt(ErrorsKey, 0);
            var warnings = SessionState.GetInt(WarningsKey, 0);
            foreach (var message in messages ?? Array.Empty<CompilerMessage>())
            {
                if (message.type == CompilerMessageType.Error) errors++;
                else if (message.type == CompilerMessageType.Warning) warnings++;
            }
            SessionState.SetInt(ErrorsKey, errors);
            SessionState.SetInt(WarningsKey, warnings);
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
