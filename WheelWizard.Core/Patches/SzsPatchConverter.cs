using static WheelWizard.Core.OperationError;
using WheelWizard.Core.Archives;
using static WheelWizard.Core.Patches.PatchConversionHelpers;

namespace WheelWizard.Core.Patches;

public sealed class SzsPatchConverter(ISzsArchiveDecoder archiveDecoder) : ISzsPatchConverter
{
    public OperationResult<PatchConversionAnalysis> AnalyzeAgainstBaseline(BaselineEntry baseline, string moddedName, byte[] moddedBytes)
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

    private OperationResult<PatchConversionAnalysis> Analyze(BaselineEntry baseline, string moddedName, byte[] moddedBytes)
    {
        if (!string.Equals(baseline.Kind, "szs", StringComparison.OrdinalIgnoreCase))
            return Fail("The selected baseline is not an SZS file.");

        var warnings = new List<ConversionMessage>();
        var skipped = new List<ConversionMessage>();
        var archiveTag = baseline.ArchiveTag;
        ArchivePath.ValidateMember(archiveTag);

        if (!StripExtension(moddedName).Equals(archiveTag, StringComparison.OrdinalIgnoreCase))
            warnings.Add(new ConversionMessage("warning.file_name_differs_from_archive_tag", archiveTag)!);

        var wholeFileHash = HashBytes64(moddedBytes);
        var wholeFileMatches = moddedBytes.Length == baseline.WholeFileSize && wholeFileHash == baseline.WholeFileHash;
        var baselineMode = baseline.Mode ?? string.Empty;

        if (baselineMode == "raw")
        {
            if (wholeFileMatches)
            {
                warnings.Add(new ConversionMessage("warning.szs_matches_baseline"));
                return new PatchConversionAnalysis
                {
                    CleanName = baseline.RelativePath,
                    ModdedName = moddedName,
                    Mode = "whole-file",
                    ArchiveTag = archiveTag,
                    Warnings = warnings,
                    Skipped = skipped,
                };
            }

            warnings.Add(new ConversionMessage("warning.whole_file_baseline"));

            return new PatchConversionAnalysis
            {
                CleanName = baseline.RelativePath,
                ModdedName = moddedName,
                Mode = "whole-file",
                ArchiveTag = archiveTag,
                Entries =
                [
                    new PatchConversionEntry(
                        $"{archiveTag}.szs",
                        baseline.RelativePath,
                        moddedBytes.ToArray(),
                        new ConversionMessage("text.whole_file_override")
                    ),
                ],
                Warnings = warnings,
                Skipped = skipped,
            };
        }

        var moddedU8Result = archiveDecoder.TryDecodeU8Archive(moddedBytes);
        if (moddedU8Result.IsFailure)
            return moddedU8Result.Error;

        var moddedU8 = moddedU8Result.Value;
        var baselineMembers = BuildBaselineMembers(baseline);
        var entries = new List<PatchConversionEntry>();

        foreach (var (logicalPath, moddedEntry) in moddedU8.Files)
        {
            baselineMembers.TryGetValue(logicalPath, out var baselineMember);

            if (IsBlockedLooseRawOverrideExtension(logicalPath))
            {
                skipped.Add(new ConversionMessage("warning.unsupported_loose_override_extension", logicalPath)!);
                continue;
            }

            if (baselineMember != null)
            {
                var moddedHash = HashBytes64(moddedEntry);

                if (baselineMember.Size == moddedEntry.Length && baselineMember.Hash == moddedHash)
                    continue;
            }

            entries.Add(
                new(
                    BuildTaggedPatchName(logicalPath, archiveTag),
                    logicalPath,
                    moddedEntry.ToArray(),
                    baselineMember == null ? new ConversionMessage("text.new_archive_member") : new ConversionMessage("text.modified_archive_member")
                )
            );
        }

        foreach (var logicalPath in baselineMembers.Keys)
        {
            if (!moddedU8.Files.ContainsKey(logicalPath))
            {
                var deletionPath = $"{logicalPath}.delete";
                entries.Add(new(BuildTaggedPatchName(deletionPath, archiveTag), deletionPath, [], new ConversionMessage("patch.detail.deleted_member")));
            }
        }

        if (entries.Count == 0 && skipped.Count == 0)
            warnings.Add(new ConversionMessage("warning.no_szs_differences"));

        return new PatchConversionAnalysis
        {
            CleanName = baseline.RelativePath,
            ModdedName = moddedName,
            Mode = "tagged-archive",
            ArchiveTag = archiveTag,
            Entries = entries.OrderBy(entry => entry.ExportPath, StringComparer.OrdinalIgnoreCase).ToArray(),
            Warnings = warnings,
            Skipped = skipped,
        };
    }

    public int EstimateDifference(BaselineEntry baseline, byte[] moddedBytes)
    {
        var wholeFileHash = HashBytes64(moddedBytes);
        if (moddedBytes.Length == baseline.WholeFileSize && wholeFileHash == baseline.WholeFileHash)
            return 0;

        var baselineMode = baseline.Mode ?? string.Empty;
        if (baselineMode == "raw")
            return 1;

        var moddedU8Result = archiveDecoder.TryDecodeU8Archive(moddedBytes);
        if (moddedU8Result.IsFailure)
            return 1;

        var moddedU8 = moddedU8Result.Value;
        var baselineMembers = BuildBaselineMembers(baseline);
        var differences = 0;

        foreach (var (logicalPath, moddedEntry) in moddedU8.Files)
        {
            baselineMembers.TryGetValue(logicalPath, out var baselineMember);
            if (baselineMember == null)
            {
                differences++;
                continue;
            }

            var moddedHash = HashBytes64(moddedEntry);
            if (baselineMember.Size != moddedEntry.Length || baselineMember.Hash != moddedHash)
                differences++;
        }

        foreach (var logicalPath in baselineMembers.Keys)
        {
            if (!moddedU8.Files.ContainsKey(logicalPath))
                differences++;
        }

        return differences;
    }

    private static Dictionary<string, BaselineMember> BuildBaselineMembers(BaselineEntry baseline)
    {
        var members = new Dictionary<string, BaselineMember>(StringComparer.Ordinal);
        if (baseline.Members == null)
            return members;

        foreach (var member in baseline.Members)
        {
            if (member.Length < 3)
                continue;

            var logicalPath = ReadJsonString(member[0]);
            if (!TryGetJsonElement(member[1], out var sizeElement) || !sizeElement.TryGetInt32(out var size))
                continue;

            var hash = ReadJsonString(member[2]);
            if (!string.IsNullOrEmpty(logicalPath) && !string.IsNullOrEmpty(hash))
                members[logicalPath] = new(size, hash);
        }

        return members;
    }

    private static string BuildTaggedPatchName(string logicalPath, string archiveTag)
    {
        ArchivePath.Validate(logicalPath);
        var segments = logicalPath.Split('/').Where(segment => segment.Length > 0 && segment != ".").ToArray();
        if (segments.Length == 0)
            throw new InvalidOperationException("Cannot build a tagged override for an empty archive path.");

        var fileName = segments[^1];
        var prefixes = string.Concat(segments[..^1].Select(segment => $"[{segment}]"));
        return $"{prefixes}{fileName}.{archiveTag}";
    }

    private static bool IsBlockedLooseRawOverrideExtension(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".kcl", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".kmp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".slt", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return string.IsNullOrEmpty(extension) ? fileName : fileName[..^extension.Length];
    }
}
