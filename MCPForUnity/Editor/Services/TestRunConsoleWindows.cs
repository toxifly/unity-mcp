using System.Collections.Generic;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.Compilation;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// Records which console entries were produced while a test run was in flight.
    ///
    /// A green run still leaves errors in the console — the ones its tests declared with
    /// LogAssert.Expect — and an agent reading the console afterwards has no way to tell those
    /// apart from real breakage. Unity's LogEntry carries no timestamp, so the runs are tracked
    /// by entry index instead: the console position at run start and at run end bound a window,
    /// and read_console(exclude_test_runs=true) drops everything inside one.
    ///
    /// State lives in SessionState because a PlayMode run reloads the domain between its own
    /// start and finish, which would wipe a plain static.
    /// </summary>
    [InitializeOnLoad]
    internal static class TestRunConsoleWindows
    {
        private const string StateKey = "MCPForUnity.TestRunConsoleWindowsV2";
        private const int MaxWindows = 10;
        private const int ClearOnPlayFlag = 1 << 1;
        private const int ClearOnRecompileFlag = 1 << 12;

        private sealed class Record
        {
            /// <summary>Console entry count as of the last observation, for clear detection.</summary>
            [JsonProperty("total")]
            public int Total { get; set; }

            /// <summary>
            /// Stable identity of the first entry at the last observation. Unlike the count,
            /// this changes when a cleared console is repopulated before we observe it.
            /// </summary>
            [JsonProperty("first")]
            public string FirstEntryIdentity { get; set; }

            /// <summary>Start index of the run currently in flight, or -1 when none is.</summary>
            [JsonProperty("pending")]
            public int Pending { get; set; } = -1;

            /// <summary>Closed windows as [start, end) index pairs, oldest first.</summary>
            [JsonProperty("windows")]
            public List<int[]> Windows { get; set; } = new();
        }

        static TestRunConsoleWindows()
        {
            CompilationPipeline.compilationStarted -= OnCompilationStarted;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        public static void MarkRunStarted()
        {
            if (ReadConsole.TryGetConsoleSnapshot(out int count, out string firstEntryIdentity))
                MarkRunStarted(count, firstEntryIdentity);
        }

        public static void MarkRunFinished()
        {
            if (ReadConsole.TryGetConsoleSnapshot(out int count, out string firstEntryIdentity))
                MarkRunFinished(count, firstEntryIdentity);
        }

        internal static void MarkRunStarted(int consoleEntryCount, string firstEntryIdentity = null)
        {
            var record = Observe(consoleEntryCount, firstEntryIdentity);
            record.Pending = consoleEntryCount;
            Save(record);
        }

        internal static void MarkRunFinished(int consoleEntryCount, string firstEntryIdentity = null)
        {
            var record = Observe(consoleEntryCount, firstEntryIdentity);
            if (record.Pending >= 0 && consoleEntryCount > record.Pending)
            {
                record.Windows.Add(new[] { record.Pending, consoleEntryCount });
                while (record.Windows.Count > MaxWindows)
                {
                    record.Windows.RemoveAt(0);
                }
            }
            record.Pending = -1;
            Save(record);
        }

        /// <summary>
        /// Folds the console's current size in without reading the windows, so a clear is noticed
        /// by every console read rather than only by callers that are filtering.
        /// </summary>
        public static void NoteConsoleSize(int currentCount)
        {
            NoteConsoleSnapshot(currentCount, null);
        }

        public static void NoteConsoleSnapshot(int currentCount, string firstEntryIdentity)
        {
            Save(Observe(currentCount, firstEntryIdentity));
        }

        /// <summary>
        /// Invalidates all closed windows when Unity tells us the console is being cleared.
        /// An in-flight run is rebased because its subsequent entries will start at index zero.
        /// </summary>
        public static void NoteConsoleCleared()
        {
            var record = Load();
            record.Windows.Clear();
            if (record.Pending >= 0)
            {
                record.Pending = 0;
            }
            record.Total = 0;
            record.FirstEntryIdentity = null;
            Save(record);
        }

        /// <summary>
        /// Half-open [start, end) console index ranges belonging to test runs, given the console's
        /// current entry count. A run still in flight contributes an open-ended window.
        /// </summary>
        public static List<int[]> GetWindows(int currentCount, string firstEntryIdentity = null)
        {
            var record = Observe(currentCount, firstEntryIdentity);
            Save(record);

            var windows = new List<int[]>(record.Windows);
            if (record.Pending >= 0)
            {
                windows.Add(new[] { record.Pending, int.MaxValue });
            }
            return windows;
        }

        public static bool Covers(IReadOnlyList<int[]> windows, int entryIndex)
        {
            for (int i = 0; i < windows.Count; i++)
            {
                if (entryIndex >= windows[i][0] && entryIndex < windows[i][1])
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Folds the current console snapshot into the record. A backwards count or a changed
        /// first-entry identity means every recorded index may now point at a different entry.
        /// Identity comparison catches clear-and-repopulate cycles whose final count is not lower.
        /// </summary>
        private static Record Observe(int count, string firstEntryIdentity)
        {
            var record = Load();
            bool firstEntryChanged = count > 0
                                     && record.Total > 0
                                     && firstEntryIdentity != null
                                     && record.FirstEntryIdentity != null
                                     && !string.Equals(
                                         firstEntryIdentity,
                                         record.FirstEntryIdentity,
                                         System.StringComparison.Ordinal);
            if (count < record.Total || firstEntryChanged)
            {
                record.Windows.Clear();
                if (record.Pending >= 0)
                {
                    // An in-flight run's entries start over from the top of the cleared console.
                    record.Pending = 0;
                }
            }
            record.Total = count;
            if (count == 0 || firstEntryIdentity != null)
            {
                record.FirstEntryIdentity = count == 0 ? null : firstEntryIdentity;
            }
            return record;
        }

        private static void OnCompilationStarted(object _)
        {
            if (ReadConsole.IsConsoleFlagEnabled(ClearOnRecompileFlag))
            {
                NoteConsoleCleared();
            }
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode
                && ReadConsole.IsConsoleFlagEnabled(ClearOnPlayFlag))
            {
                NoteConsoleCleared();
            }
        }

        private static Record Load()
        {
            string raw = SessionState.GetString(StateKey, string.Empty);
            if (string.IsNullOrEmpty(raw)) return new Record();
            try
            {
                return JsonConvert.DeserializeObject<Record>(raw) ?? new Record();
            }
            catch (JsonException)
            {
                return new Record();
            }
        }

        private static void Save(Record record)
        {
            SessionState.SetString(StateKey, JsonConvert.SerializeObject(record));
        }
    }
}
