using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MCPForUnity.Editor.Services.MutationTransactions
{
    /// <summary>
    /// Merges a freshly serialized scene ("temp": the in-memory truth) into the on-disk scene
    /// file at YAML block granularity. Blocks (objects) that are new or were intentionally
    /// mutated (per <see cref="SceneMutationLedger"/>) are taken from the temp serialization;
    /// every other block keeps its on-disk bytes, so ambient in-memory drift is never baked in.
    /// Pure string-in/string-out so it is unit-testable without a Unity scene.
    /// </summary>
    public static class SceneYamlMerge
    {
        private const int GameObjectClassId = 1;
        private const int TransformClassId = 4;
        private const int RectTransformClassId = 224;
        private const int SceneRootsClassId = 1660057539;

        public sealed class MergeResult
        {
            public string MergedText;
            public int BlocksTotal;
            public int BlocksNew;
            public int BlocksScoped;
            public int BlocksDeleted;
            public int BlocksDriftSuppressed;
            public List<long> DriftSuppressedIds = new List<long>();
            public List<long> UnscopedDeletionsKept = new List<long>();
            public List<string> Warnings = new List<string>();
        }

        private enum BlockSource
        {
            TempNew,
            TempScoped,
            DiskKept,
            DroppedDeleted,
            DiskKeptUnscopedDeletion,
        }

        private sealed class YamlBlock
        {
            public int ClassId;
            public long FileId;
            public string Text;             // full block text incl. header line, '\n'-terminated
            public long PrefabInstanceId;   // stripped/instance-member blocks reference their PrefabInstance
        }

        private static readonly Regex HeaderRegex = new Regex(
            @"^--- !u!(\d+) &(-?\d+)( stripped)?[ \t]*\r?$",
            RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Regex PrefabInstanceRefRegex = new Regex(
            @"^  m_PrefabInstance: \{fileID: (-?\d+)\}", RegexOptions.Multiline | RegexOptions.Compiled);

        // Scene-local object references have no guid/type suffix. Asset references can reuse the
        // same numeric fileID in another file, so intentionally leave those compound mappings alone.
        private static readonly Regex LocalObjectRefRegex = new Regex(
            @"\{fileID: (-?\d+)\}", RegexOptions.Compiled);

        public static MergeResult Merge(
            string diskText,
            string tempText,
            HashSet<long> scopedFileIds,
            HashSet<long> deletedFileIds)
        {
            diskText = Normalize(diskText);
            tempText = Normalize(tempText);
            (string header, List<YamlBlock> diskBlocks) = Parse(diskText);
            (string _, List<YamlBlock> tempBlocks) = Parse(tempText);
            Dictionary<long, YamlBlock> diskById = diskBlocks.ToDictionary(b => b.FileId);
            Dictionary<long, YamlBlock> tempById = tempBlocks.ToDictionary(b => b.FileId);

            var source = new Dictionary<long, BlockSource>();
            foreach (YamlBlock block in tempBlocks)
            {
                bool scoped = scopedFileIds.Contains(block.FileId)
                    || (block.PrefabInstanceId != 0 && scopedFileIds.Contains(block.PrefabInstanceId));
                source[block.FileId] = !diskById.ContainsKey(block.FileId) ? BlockSource.TempNew
                    : scoped ? BlockSource.TempScoped
                    : BlockSource.DiskKept;
            }
            foreach (YamlBlock block in diskBlocks.Where(b => !tempById.ContainsKey(b.FileId)))
            {
                bool deleted = deletedFileIds.Contains(block.FileId)
                    || (block.PrefabInstanceId != 0 && deletedFileIds.Contains(block.PrefabInstanceId));
                source[block.FileId] = deleted ? BlockSource.DroppedDeleted : BlockSource.DiskKeptUnscopedDeletion;
            }

            // Existing blocks keep their DISK order so an effectively-unchanged save is
            // byte-identical (temp order is Unity's canonical re-sort — emitting it would churn
            // the file with pure block moves). New blocks anchor after their temp-order
            // predecessor that exists on disk.
            var newAtStart = new List<YamlBlock>();
            var newAfterAnchor = new Dictionary<long, List<YamlBlock>>();
            long anchor = 0;
            bool haveAnchor = false;
            foreach (YamlBlock block in tempBlocks)
            {
                if (source[block.FileId] == BlockSource.TempNew)
                {
                    if (!haveAnchor)
                        newAtStart.Add(block);
                    else if (newAfterAnchor.TryGetValue(anchor, out List<YamlBlock> queued))
                        queued.Add(block);
                    else
                        newAfterAnchor[anchor] = new List<YamlBlock> { block };
                }
                else
                {
                    anchor = block.FileId;
                    haveAnchor = true;
                }
            }

            var result = new MergeResult();
            var output = new StringBuilder(diskText.Length);
            output.Append(header);
            foreach (YamlBlock block in newAtStart)
            {
                output.Append(block.Text);
                result.BlocksNew++;
            }
            foreach (YamlBlock diskBlock in diskBlocks)
            {
                AppendExistingBlock(diskBlock, tempById, source, output, result);
                if (newAfterAnchor.TryGetValue(diskBlock.FileId, out List<YamlBlock> queued))
                    foreach (YamlBlock block in queued)
                    {
                        output.Append(block.Text);
                        result.BlocksNew++;
                    }
            }

            result.MergedText = output.ToString();
            result.BlocksTotal = tempBlocks.Count;
            return result;
        }

        private static void AppendExistingBlock(
            YamlBlock diskBlock,
            Dictionary<long, YamlBlock> tempById,
            Dictionary<long, BlockSource> source,
            StringBuilder output,
            MergeResult result)
        {
            switch (source[diskBlock.FileId])
            {
                case BlockSource.DroppedDeleted:
                    result.BlocksDeleted++;
                    break;
                case BlockSource.TempScoped:
                    output.Append(ReconcileDeletedReferences(
                        diskBlock,
                        ReconcileOwnerReferences(
                            diskBlock, tempById[diskBlock.FileId], source, result, useTempBytes: true),
                        source,
                        result));
                    result.BlocksScoped++;
                    break;
                case BlockSource.DiskKeptUnscopedDeletion:
                    // The object vanished in memory but the deletion was never recorded as
                    // intentional — keep the disk block instead of silently deleting it.
                    output.Append(ReconcileDeletedReferences(diskBlock, diskBlock.Text, source, result));
                    result.UnscopedDeletionsKept.Add(diskBlock.FileId);
                    break;
                case BlockSource.DiskKept:
                default:
                    YamlBlock tempBlock = tempById[diskBlock.FileId];
                    string emitted = ReconcileOwnerReferences(
                        diskBlock, tempBlock, source, result, useTempBytes: false);
                    emitted = ReconcileDeletedReferences(diskBlock, emitted, source, result);
                    // Drift = in-memory state the merge refuses to write. Compare against the
                    // emitted text, not the raw disk text: a child-list change the reconciler
                    // accepted (e.g. a new MCP root landing in SceneRoots.m_Roots) is intentional,
                    // not drift.
                    if (!string.Equals(emitted, tempBlock.Text, StringComparison.Ordinal))
                    {
                        result.BlocksDriftSuppressed++;
                        result.DriftSuppressedIds.Add(diskBlock.FileId);
                    }
                    output.Append(emitted);
                    break;
            }
        }

        /// <summary>
        /// Nulls scene-local references to blocks that this merge intentionally drops. Unity
        /// clears those references in the temp serialization when an object is destroyed, but an
        /// otherwise-unscoped referencing block keeps its disk bytes. Splice only the affected
        /// inline reference so unrelated ambient changes in the temp block remain suppressed.
        /// </summary>
        private static string ReconcileDeletedReferences(
            YamlBlock owner,
            string blockText,
            Dictionary<long, BlockSource> source,
            MergeResult result)
        {
            int reconciled = 0;
            string emitted = LocalObjectRefRegex.Replace(blockText, match =>
            {
                if (!long.TryParse(match.Groups[1].Value, out long referencedId)
                    || !source.TryGetValue(referencedId, out BlockSource referencedSource)
                    || referencedSource != BlockSource.DroppedDeleted)
                    return match.Value;

                reconciled++;
                return "{fileID: 0}";
            });

            if (reconciled > 0)
            {
                result.Warnings.Add(
                    $"cleared {reconciled} reference(s) to scoped deletions on &{owner.FileId}");
            }
            return emitted;
        }

        /// <summary>
        /// An owner block may still need its reference list reconciled. New/scoped members exist
        /// only in the temp list, scoped-deleted members linger in the disk list, and an unscoped
        /// deletion must retain both the disk block and its owner reference. Reconciles
        /// GameObject.m_Component, Transform.m_Children, and SceneRoots.m_Roots entry by entry,
        /// leaving every other byte from the selected source untouched.
        /// </summary>
        private static string ReconcileOwnerReferences(
            YamlBlock diskBlock,
            YamlBlock tempBlock,
            Dictionary<long, BlockSource> source,
            MergeResult result,
            bool useTempBytes)
        {
            string field = diskBlock.ClassId == GameObjectClassId ? "m_Component"
                : diskBlock.ClassId == TransformClassId || diskBlock.ClassId == RectTransformClassId
                    ? "m_Children"
                    : diskBlock.ClassId == SceneRootsClassId ? "m_Roots" : null;
            if (field == null)
                return useTempBytes ? tempBlock.Text : diskBlock.Text;

            bool componentEntries = diskBlock.ClassId == GameObjectClassId;

            if (!TryParseListField(
                    diskBlock.Text, field, componentEntries,
                    out List<long> diskList, out int diskStart, out int diskLength)
                || !TryParseListField(
                    tempBlock.Text, field, componentEntries,
                    out List<long> tempList, out int tempStart, out int tempLength))
                return useTempBytes ? tempBlock.Text : diskBlock.Text;

            List<long> desired = ReconcileList(tempList, diskList, source);
            List<long> emittedList = useTempBytes ? tempList : diskList;
            if (desired.SequenceEqual(emittedList))
                return useTempBytes ? tempBlock.Text : diskBlock.Text;

            var rendered = new StringBuilder();
            rendered.Append("  ").Append(field).Append(':');
            if (desired.Count == 0)
                rendered.Append(" []\n");
            else
            {
                rendered.Append('\n');
                foreach (long id in desired)
                {
                    rendered.Append(componentEntries ? "  - component: {fileID: " : "  - {fileID: ")
                        .Append(id).Append("}\n");
                }
            }
            result.Warnings.Add(
                $"spliced {field} on &{diskBlock.FileId} ({emittedList.Count} -> {desired.Count} entries)");
            string baseText = useTempBytes ? tempBlock.Text : diskBlock.Text;
            int start = useTempBytes ? tempStart : diskStart;
            int length = useTempBytes ? tempLength : diskLength;
            return baseText.Substring(0, start) + rendered + baseText.Substring(start + length);
        }

        private static List<long> ReconcileList(
            List<long> tempList,
            List<long> diskList,
            Dictionary<long, BlockSource> source)
        {
            BlockSource KindOf(long id) => source.TryGetValue(id, out BlockSource k) ? k : BlockSource.DroppedDeleted;

            // Unscoped entries keep their DISK order — an ambient sibling reorder is drift, and
            // an unscoped deletion must not shuffle the kept survivor to the end of the list.
            // Disk entries stay while the child's DISK block (which still claims this parent)
            // lands in the output; scoped entries re-anchor from temp order below.
            var spine = diskList.Where(id =>
            {
                BlockSource kind = KindOf(id);
                return kind == BlockSource.DiskKept || kind == BlockSource.DiskKeptUnscopedDeletion;
            }).ToList();

            // New/scoped entries are the only movable ones: only their TEMP block (whose parent
            // pointer claims this parent) lands in the output, so they take their temp position,
            // anchored after their nearest temp-order predecessor that sits on the disk spine.
            var spineSet = new HashSet<long>(spine);
            var atStart = new List<long>();
            var afterAnchor = new Dictionary<long, List<long>>();
            long anchor = 0;
            bool haveAnchor = false;
            foreach (long id in tempList)
            {
                if (spineSet.Contains(id))
                {
                    anchor = id;
                    haveAnchor = true;
                    continue;
                }
                BlockSource kind = KindOf(id);
                if (kind != BlockSource.TempNew && kind != BlockSource.TempScoped)
                    continue;
                if (!haveAnchor)
                    atStart.Add(id);
                else if (afterAnchor.TryGetValue(anchor, out List<long> queued))
                    queued.Add(id);
                else
                    afterAnchor[anchor] = new List<long> { id };
            }

            var desired = new List<long>(atStart);
            foreach (long id in spine)
            {
                desired.Add(id);
                if (afterAnchor.TryGetValue(id, out List<long> queued))
                    desired.AddRange(queued);
            }
            return desired;
        }

        private static bool TryParseListField(
            string blockText,
            string field,
            bool componentEntries,
            out List<long> ids,
            out int start,
            out int length)
        {
            ids = null;
            start = 0;
            length = 0;
            var fieldRegex = new Regex($@"^  {Regex.Escape(field)}:( \[\])?$", RegexOptions.Multiline);
            Match fieldMatch = fieldRegex.Match(blockText);
            if (!fieldMatch.Success)
                return false;

            ids = new List<long>();
            start = fieldMatch.Index;
            int cursor = blockText.IndexOf('\n', fieldMatch.Index) + 1;
            if (fieldMatch.Groups[1].Success)
            {
                length = cursor - start;
                return true; // "m_Children: []"
            }
            var entryRegex = componentEntries
                ? new Regex(@"^  - component: \{fileID: (-?\d+)\}$")
                : new Regex(@"^  - \{fileID: (-?\d+)\}$");
            while (cursor < blockText.Length)
            {
                int lineEnd = blockText.IndexOf('\n', cursor);
                if (lineEnd < 0)
                    lineEnd = blockText.Length - 1;
                Match entry = entryRegex.Match(blockText.Substring(cursor, lineEnd - cursor));
                if (!entry.Success)
                    break;
                ids.Add(long.Parse(entry.Groups[1].Value));
                cursor = lineEnd + 1;
            }
            length = cursor - start;
            return true;
        }

        private static (string header, List<YamlBlock> blocks) Parse(string text)
        {
            MatchCollection matches = HeaderRegex.Matches(text);
            if (matches.Count == 0)
                throw new FormatException("Scene text contains no '--- !u!' object blocks.");

            string header = text.Substring(0, matches[0].Index);
            var blocks = new List<YamlBlock>(matches.Count);
            for (int i = 0; i < matches.Count; i++)
            {
                int blockStart = matches[i].Index;
                int blockEnd = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
                string blockText = text.Substring(blockStart, blockEnd - blockStart);
                Match prefabRef = PrefabInstanceRefRegex.Match(blockText);
                blocks.Add(new YamlBlock
                {
                    ClassId = int.Parse(matches[i].Groups[1].Value),
                    FileId = long.Parse(matches[i].Groups[2].Value),
                    Text = blockText,
                    PrefabInstanceId = prefabRef.Success ? long.Parse(prefabRef.Groups[1].Value) : 0,
                });
            }
            return (header, blocks);
        }

        private static string Normalize(string text) => text.Replace("\r\n", "\n");
    }
}
