using WheelWizard.Core.Helpers;
using System.Text;
using static WheelWizard.Core.Patches.PatchConversionHelpers;

namespace WheelWizard.Core.Patches;

public static class BrsarPatchConverter
{
    private const int HeaderScanWindow = 0x4000;
    private static readonly Encoding Utf8 = Encoding.UTF8;

    public static OperationResult<PatchConversionAnalysis> AnalyzeAgainstBaseline(BaselineEntry baseline, string moddedName, byte[] moddedBytes)
    {
        try
        {
            return Analyze(baseline, moddedName, moddedBytes);
        }
        catch (Exception ex)
        {
            return new OperationError { Message = $"Failed to analyze '{moddedName}': {ex.Message}", Exception = ex };
        }
    }

    private static OperationResult<PatchConversionAnalysis> Analyze(BaselineEntry baseline, string moddedName, byte[] moddedBytes)
    {
        if (!string.Equals(baseline.Kind, "brsar", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected baseline is not a BRSAR file.");

        var warnings = new List<ConversionMessage>();
        var skipped = new List<ConversionMessage>();
        var moddedParse = ParseBrsar(moddedBytes);
        var baselineEntries = BuildBaselineEntries(baseline);
        var allFileIds = baselineEntries.Keys.Concat(moddedParse.Entries.Keys).Distinct().Order().ToArray();
        var entries = new List<PatchConversionEntry>();

        foreach (var fileId in allFileIds)
        {
            baselineEntries.TryGetValue(fileId, out var baselineEntry);
            moddedParse.Entries.TryGetValue(fileId, out var moddedEntry);

            if (baselineEntry == null || moddedEntry == null)
                continue;

            var moddedKind = GetBaselineKindCode(moddedEntry.Kind);
            var moddedMagic = moddedEntry.Magic;
            var moddedHash = HashBytes64(moddedEntry.CompareBytes);
            if (
                baselineEntry.Kind == moddedKind
                && baselineEntry.Magic == moddedMagic
                && baselineEntry.Size == moddedEntry.CompareBytes.Length
                && baselineEntry.Hash == moddedHash
            )
            {
                continue;
            }

            if (moddedEntry is { Kind: BrsarEntryKind.Supported, ExportBytes: not null })
            {
                var extension = GetBrsarEntryExtension(moddedEntry.Magic);
                entries.Add(
                    new($"{fileId}{extension}", $"revo_kart.brsar:{fileId}", moddedEntry.ExportBytes.ToArray(), moddedEntry.Detail)
                );
                continue;
            }

            if (moddedEntry.Kind == BrsarEntryKind.Unsupported)
            {
                skipped.Add(new ConversionMessage("warning.brsar_file_id_unsupported_magic", fileId, moddedEntry.Magic ?? string.Empty)!);
                continue;
            }

            if (moddedEntry.Kind == BrsarEntryKind.External)
            {
                skipped.Add(new ConversionMessage("warning.brsar_file_id_external", fileId)!);
                continue;
            }

            skipped.Add(new ConversionMessage("warning.brsar_file_id_unresolved", fileId)!);
        }

        var unsupportedSummary = SummarizeUnsupportedBrsarCounts(moddedParse.UnsupportedCounts);
        if (unsupportedSummary != null)
            warnings.Add(new ConversionMessage("warning.brsar_unsupported_summary", unsupportedSummary)!);
        if (moddedParse.ExternalCount > 0)
            warnings.Add(new ConversionMessage("warning.brsar_external_count", moddedParse.ExternalCount)!);
        if (moddedParse.UnresolvedCount > 0)
            warnings.Add(new ConversionMessage("warning.brsar_unresolved_count", moddedParse.UnresolvedCount)!);
        if (entries.Count == 0 && skipped.Count == 0)
            warnings.Add(new ConversionMessage("warning.brsar_no_supported_differences"));

        return new PatchConversionAnalysis()
        {
            CleanName = baseline.RelativePath,
            ModdedName = moddedName,
            Mode = "brsar",
            ArchiveTag = "revo_kart",
            Entries = entries.OrderBy(entry => entry.ExportPath, StringComparer.OrdinalIgnoreCase).ToArray(),
            Warnings = warnings,
            Skipped = skipped,
        };
    }

    public static int EstimateDifference(BaselineEntry baseline, byte[] moddedBytes)
    {
        BrsarParseResult moddedParse;

        try
        {
            moddedParse = ParseBrsar(moddedBytes);
        }
        catch
        {
            return 1;
        }

        var baselineEntries = BuildBaselineEntries(baseline);
        var allFileIds = baselineEntries.Keys.Concat(moddedParse.Entries.Keys).Distinct();
        var differences = 0;

        foreach (var fileId in allFileIds)
        {
            baselineEntries.TryGetValue(fileId, out var baselineEntry);
            moddedParse.Entries.TryGetValue(fileId, out var moddedEntry);
            if (baselineEntry == null || moddedEntry == null)
                continue;

            var moddedKind = GetBaselineKindCode(moddedEntry.Kind);
            var moddedMagic = moddedEntry.Magic;
            var moddedHash = HashBytes64(moddedEntry.CompareBytes);
            if (
                baselineEntry.Kind != moddedKind
                || baselineEntry.Magic != moddedMagic
                || baselineEntry.Size != moddedEntry.CompareBytes.Length
                || baselineEntry.Hash != moddedHash
            )
            {
                differences++;
            }
        }

        return differences;
    }

    private static Dictionary<int, BaselineBrsarEntry> BuildBaselineEntries(BaselineEntry baseline)
    {
        var entries = new Dictionary<int, BaselineBrsarEntry>();
        if (baseline.Entries == null)
            return entries;

        foreach (var entry in baseline.Entries)
        {
            if (entry.Length < 5)
                continue;

            if (!TryGetJsonElement(entry[0], out var fileIdElement) || !fileIdElement.TryGetInt32(out var fileId))
                continue;

            var kind = ReadJsonString(entry[1]);
            var magic = ReadJsonString(entry[2]);

            if (!TryGetJsonElement(entry[3], out var sizeElement) || !sizeElement.TryGetInt32(out var size))
                continue;

            var hash = ReadJsonString(entry[4]);

            if (!string.IsNullOrEmpty(kind) && !string.IsNullOrEmpty(hash))
                entries[fileId] = new(kind, magic, size, hash);
        }

        return entries;
    }

    private static BrsarParseResult ParseBrsar(byte[] bytes)
    {
        if (BinaryStringHelper.ReadAscii(bytes, 0, 4) != "RSAR")
            throw new InvalidDataException("The selected BRSAR file does not start with the RSAR header.");

        var infoOffset = BigEndianBinaryHelper.BufferToInt32(bytes, 0x18);
        var infoBase = checked(infoOffset + 0x08);
        var fileTableOffset = ResolveDataRef(bytes, infoBase + 0x18, infoBase);
        var groupTableOffset = ResolveDataRef(bytes, infoBase + 0x20, infoBase);

        if (fileTableOffset == null || groupTableOffset == null)
            throw new InvalidDataException("The BRSAR file is missing the INFO file/group tables.");

        var (knownHeaders, rwarHeaders) = ScanBrsarHeaders(bytes);
        var groupEntryOffsets = ParseReferenceTable(bytes, groupTableOffset.Value, infoBase);
        var groups = groupEntryOffsets.Select((offset, groupIndex) => ParseGroupInfo(bytes, offset, infoBase, groupIndex)).ToArray();
        var fileEntryOffsets = ParseReferenceTable(bytes, fileTableOffset.Value, infoBase);
        var entries = new Dictionary<int, BrsarEntry>();
        var unsupportedCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var externalCount = 0;
        var unresolvedCount = 0;

        for (var fileId = 0; fileId < fileEntryOffsets.Count; fileId++)
        {
            var entryOffset = fileEntryOffsets[fileId];
            var declaredFileSize = BigEndianBinaryHelper.BufferToInt32(bytes, entryOffset);
            var declaredWaveSize = BigEndianBinaryHelper.BufferToInt32(bytes, entryOffset + 0x04);
            if (declaredFileSize < 0 || declaredWaveSize < 0)
                throw new InvalidDataException($"BRSAR sound {fileId} at offset {entryOffset} has negative file/wave size.");
            var externalNameRef = ResolveDataRef(bytes, entryOffset + 0x0c, infoBase);
            if (externalNameRef != null)
            {
                externalCount++;
                var externalPath = BinaryStringHelper.ReadNullTerminatedAscii(bytes, externalNameRef.Value);
                entries[fileId] = new(
                    BrsarEntryKind.External,
                    Utf8.GetBytes(externalPath),
                    null,
                    null,
                    externalPath.Length > 0 ? new ConversionMessage("text.external_reference_with_path", externalPath)! : new ConversionMessage("text.external_reference")
                );
                continue;
            }

            var positionTableOffset = ResolveDataRef(bytes, entryOffset + 0x14, infoBase);
            if (positionTableOffset == null)
            {
                unresolvedCount++;
                entries[fileId] = BrsarEntry.Unresolved("Missing file-position table");
                continue;
            }

            var positions = ParseReferenceTable(bytes, positionTableOffset.Value, infoBase);
            if (positions.Count == 0)
            {
                unresolvedCount++;
                entries[fileId] = BrsarEntry.Unresolved("Empty file-position table");
                continue;
            }

            var firstPosition = positions[0];
            var groupIndex = BigEndianBinaryHelper.BufferToInt32(bytes, firstPosition);
            var itemIndex = BigEndianBinaryHelper.BufferToInt32(bytes, firstPosition + 4);
            var group = groupIndex >= 0 && groupIndex < groups.Length ? groups[groupIndex] : null;

            if (group == null || itemIndex < 0 || itemIndex >= group.ItemOffsets.Count)
            {
                unresolvedCount++;
                entries[fileId] = BrsarEntry.Unresolved("Invalid group/item reference");
                continue;
            }

            var itemOffset = group.ItemOffsets[itemIndex];
            var fileRelativeOffset = BigEndianBinaryHelper.BufferToInt32(bytes, itemOffset + 0x04);
            var audioRelativeOffset = BigEndianBinaryHelper.BufferToInt32(bytes, itemOffset + 0x0c);
            var mainGuess = checked(group.FileDataOffset + fileRelativeOffset);
            var waveGuess = checked(group.AudioDataOffset + audioRelativeOffset);
            var mainHeader = FindNearestBrsarHeader(knownHeaders, mainGuess, declaredFileSize);

            if (mainHeader == null)
            {
                unresolvedCount++;
                entries[fileId] = BrsarEntry.Unresolved("No nearby embedded sound header was found");
                continue;
            }

            var mainBytes = SliceBytes(bytes, mainHeader.Offset, mainHeader.Offset + mainHeader.Size);
            if (!IsSupportedBrsarMagic(mainHeader.Magic))
            {
                unsupportedCounts[mainHeader.Magic] = unsupportedCounts.GetValueOrDefault(mainHeader.Magic) + 1;
                entries[fileId] = new(
                    BrsarEntryKind.Unsupported,
                    mainBytes,
                    null,
                    mainHeader.Magic,
                    new ConversionMessage("text.brsar_entry", mainHeader.Magic, fileId)!
                );
                continue;
            }

            var exportBytes = mainBytes;
            var detail = new ConversionMessage("text.brsar_entry", mainHeader.Magic, fileId)!;
            var waveHeader = FindNearestRwarHeader(rwarHeaders, waveGuess, declaredWaveSize, mainHeader.Offset - mainGuess);
            if (declaredWaveSize > 0 && waveHeader != null)
            {
                var waveBytes = SliceBytes(bytes, waveHeader.Offset, waveHeader.Offset + waveHeader.Size);
                exportBytes = JoinWithAlignment(mainBytes, waveBytes, 0x20);
                detail = new ConversionMessage("text.brsar_entry_with_rwar", mainHeader.Magic, fileId)!;
            }

            entries[fileId] = new(BrsarEntryKind.Supported, exportBytes, exportBytes, mainHeader.Magic, detail);
        }

        return new(entries, externalCount, unresolvedCount, unsupportedCounts);
    }

    private static GroupInfo ParseGroupInfo(byte[] bytes, int entryOffset, int infoOffset, int groupIndex)
    {
        var itemTableOffset = ResolveDataRef(bytes, entryOffset + 0x20, infoOffset);
        if (itemTableOffset == null)
            throw new InvalidDataException($"Group {groupIndex} does not contain an item table.");

        return new(
            BigEndianBinaryHelper.BufferToInt32(bytes, entryOffset + 0x10),
            BigEndianBinaryHelper.BufferToInt32(bytes, entryOffset + 0x18),
            ParseReferenceTable(bytes, itemTableOffset.Value, infoOffset)
        );
    }

    private static List<int> ParseReferenceTable(byte[] bytes, int tableOffset, int baseAddress)
    {
        var count = BigEndianBinaryHelper.BufferToInt32(bytes, tableOffset);
        if (count < 0 || tableOffset < 0 || tableOffset > bytes.Length - 4
            || count > (bytes.Length - tableOffset - 4) / 8)
            throw new InvalidDataException($"BRSAR reference table at offset {tableOffset} has invalid count {count}.");
        var offsets = new List<int>();

        for (var index = 0; index < count; index++)
        {
            var target = ResolveDataRef(bytes, tableOffset + 4 + index * 8, baseAddress);
            // Omitting a table slot would silently renumber subsequent sound IDs.
            if (target == null)
                throw new InvalidDataException($"BRSAR reference table at offset {tableOffset} has an empty slot {index}.");
            offsets.Add(target.Value);
        }

        return offsets;
    }

    private static int? ResolveDataRef(byte[] bytes, int refOffset, int baseAddress)
    {
        if (refOffset < 0 || refOffset > bytes.Length - 8)
            throw new InvalidDataException($"BRSAR reference at offset {refOffset} is truncated.");

        var refType = bytes[refOffset];
        var value = BigEndianBinaryHelper.BufferToInt32(bytes, refOffset + 4);
        if (value == 0)
            return null;
        var target = refType switch
        {
            0 => value,
            1 => checked(baseAddress + value),
            _ => throw new InvalidDataException($"Unsupported BRSAR data reference type {refType} at offset {refOffset}."),
        };
        if (target < 0 || target >= bytes.Length)
            throw new InvalidDataException($"BRSAR reference at offset {refOffset} points outside the file: {target}.");
        return target;
    }

    private static (List<KnownBrsarHeader> KnownHeaders, List<RwarHeader> RwarHeaders) ScanBrsarHeaders(byte[] bytes)
    {
        var knownHeaders = new List<KnownBrsarHeader>();
        var rwarHeaders = new List<RwarHeader>();

        for (var offset = 0; offset + 0x10 <= bytes.Length; offset++)
        {
            if (bytes[offset] != 0x52)
                continue;

            var magic = BinaryStringHelper.ReadAscii(bytes, offset, 4);
            if (magic is not ("RWSD" or "RBNK" or "RWAV" or "RSEQ" or "RSTM" or "RWAR"))
                continue;

            var size = BigEndianBinaryHelper.BufferToInt32(bytes, offset + 0x08);
            if (size < 0x20 || size > bytes.Length - offset)
                continue;

            if (magic == "RWAR")
                rwarHeaders.Add(new(offset, size));
            else
                knownHeaders.Add(new(magic, offset, size));
        }

        return (knownHeaders, rwarHeaders);
    }

    private static KnownBrsarHeader? FindNearestBrsarHeader(List<KnownBrsarHeader> headers, int guessOffset, int expectedSize)
    {
        if (headers.Count == 0)
            return null;

        KnownBrsarHeader? bestHeader = null;
        var bestScore = double.PositiveInfinity;
        var startIndex = LowerBoundHeaderOffset(headers, guessOffset);

        for (var left = startIndex - 1; left >= 0; left--)
        {
            var header = headers[left];
            var distance = guessOffset - header.Offset;
            if (distance > HeaderScanWindow)
                break;

            var score = distance + Math.Min(Math.Abs(header.Size - expectedSize), 0x10000) / 16.0;
            if (score < bestScore)
            {
                bestScore = score;
                bestHeader = header;
            }
        }

        for (var right = startIndex; right < headers.Count; right++)
        {
            var header = headers[right];
            var distance = header.Offset - guessOffset;
            if (distance > HeaderScanWindow)
                break;

            var score = distance + Math.Min(Math.Abs(header.Size - expectedSize), 0x10000) / 16.0;
            if (score < bestScore)
            {
                bestScore = score;
                bestHeader = header;
            }
        }

        return bestHeader;
    }

    private static RwarHeader? FindNearestRwarHeader(List<RwarHeader> headers, int guessOffset, int expectedSize, int preferredDelta)
    {
        if (headers.Count == 0)
            return null;

        RwarHeader? bestHeader = null;
        var bestScore = double.PositiveInfinity;
        var startIndex = LowerBoundHeaderOffset(headers, guessOffset);

        for (var left = startIndex - 1; left >= 0; left--)
        {
            var header = headers[left];
            var distance = guessOffset - header.Offset;
            if (distance > HeaderScanWindow)
                break;

            var relativePenalty = Math.Abs((header.Offset - guessOffset) - preferredDelta) / 2.0;
            var sizePenalty = Math.Min(Math.Abs(header.Size - expectedSize), 0x20000) / 32.0;
            var score = distance + relativePenalty + sizePenalty;
            if (score < bestScore)
            {
                bestScore = score;
                bestHeader = header;
            }
        }

        for (var right = startIndex; right < headers.Count; right++)
        {
            var header = headers[right];
            var distance = header.Offset - guessOffset;
            if (distance > HeaderScanWindow)
                break;

            var relativePenalty = Math.Abs((header.Offset - guessOffset) - preferredDelta) / 2.0;
            var sizePenalty = Math.Min(Math.Abs(header.Size - expectedSize), 0x20000) / 32.0;
            var score = distance + relativePenalty + sizePenalty;
            if (score < bestScore)
            {
                bestScore = score;
                bestHeader = header;
            }
        }

        return bestHeader;
    }

    private static int LowerBoundHeaderOffset<T>(List<T> headers, int guessOffset)
        where T : IHeaderOffset
    {
        var low = 0;
        var high = headers.Count;

        while (low < high)
        {
            var middle = (low + high) >> 1;
            if (headers[middle].Offset < guessOffset)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    private static string? SummarizeUnsupportedBrsarCounts(Dictionary<string, int> counts)
    {
        var parts = counts
            .Where(entry => entry.Value > 0)
            .OrderByDescending(entry => entry.Value)
            .Select(entry => $"{entry.Value} {entry.Key}")
            .ToArray();

        return parts.Length == 0 ? null : string.Join(", ", parts);
    }

    private static string GetBaselineKindCode(BrsarEntryKind kind) =>
        kind switch
        {
            BrsarEntryKind.Supported => "s",
            BrsarEntryKind.Unsupported => "u",
            BrsarEntryKind.External => "e",
            _ => "n",
        };

    private static bool IsSupportedBrsarMagic(string magic) => magic is "RBNK" or "RSEQ" or "RWSD";

    private static string GetBrsarEntryExtension(string? magic) =>
        magic switch
        {
            "RBNK" => ".brbnk",
            "RSEQ" => ".brseq",
            "RWSD" => ".brwsd",
            _ => throw new InvalidOperationException("Cannot export a BRSAR entry without a supported magic."),
        };

    private static byte[] JoinWithAlignment(byte[] first, byte[] second, int alignment)
    {
        var padding = (alignment - (first.Length % alignment)) % alignment;
        var output = new byte[first.Length + padding + second.Length];
        Buffer.BlockCopy(first, 0, output, 0, first.Length);
        Buffer.BlockCopy(second, 0, output, first.Length + padding, second.Length);
        return output;
    }

    private static byte[] SliceBytes(byte[] bytes, int start, int end)
    {
        if (start < 0 || end < start || end > bytes.Length)
            throw new InvalidDataException("Tried to read outside the selected file.");

        return bytes[start..end];
    }

    private enum BrsarEntryKind
    {
        Supported,
        Unsupported,
        External,
        Unresolved,
    }

    private sealed record BrsarParseResult(
        Dictionary<int, BrsarEntry> Entries,
        int ExternalCount,
        int UnresolvedCount,
        Dictionary<string, int> UnsupportedCounts
    );

    private sealed record BrsarEntry(BrsarEntryKind Kind, byte[] CompareBytes, byte[]? ExportBytes, string? Magic, ConversionMessage Detail)
    {
        public static BrsarEntry Unresolved(string detail) => new(BrsarEntryKind.Unresolved, [], null, null, new ConversionMessage("patch.detail.unresolved", detail));
    }

    private sealed record GroupInfo(int FileDataOffset, int AudioDataOffset, List<int> ItemOffsets);

    private interface IHeaderOffset
    {
        int Offset { get; }
    }

    private sealed record KnownBrsarHeader(string Magic, int Offset, int Size) : IHeaderOffset;

    private sealed record RwarHeader(int Offset, int Size) : IHeaderOffset;
}
