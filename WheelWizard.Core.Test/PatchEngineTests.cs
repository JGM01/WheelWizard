using System.Text;
using System.Text.Json;
using WheelWizard.Core.Archives;
using WheelWizard.Core.Helpers;
using WheelWizard.Core.Patches;

namespace WheelWizard.Core.Test;

public sealed class PatchEngineTests
{
    private readonly SzsArchiveDecoder decoder = new();
    private SzsPatchConverter Converter => new(decoder);

    [Fact]
    public void EmbeddedBaselinesAreOwnedByCoreAndRetainSelectionData()
    {
        var store = GameBaselineStore.Instance;
        var candidates = store.FindCandidates("Race.szs", "szs");
        Assert.Contains(candidates, c => c.Region == "Europe" || c.Id.Contains("europe"));
        foreach (var candidate in candidates)
            Assert.Equal("szs", store.GetEntry(candidate.Id)!.Kind);
        Assert.Empty(store.FindCandidates("Race.szs", "brsar"));
        Assert.Null(store.GetEntry("missing"));
        Assert.DoesNotContain(typeof(SzsPatchConverter).Assembly.GetReferencedAssemblies(),
            a => a.Name == "WheelWizard" || a.Name!.StartsWith("Avalonia"));
    }

    [Fact]
    public void ArchiveRoundTripsAreDeterministicAndPreserveMembers()
    {
        var entries = new Dictionary<string, byte[]> { ["z.bin"] = [1, 2, 3], ["dir/a.bin"] = new byte[900] };
        var raw = U8ArchiveBuilder.Build(entries);
        var compressed = U8ArchiveBuilder.BuildYaz0(entries);
        Assert.Equal(compressed, U8ArchiveBuilder.BuildYaz0(entries.Reverse()));
        foreach (var archive in new[] { raw, compressed })
        {
            var result = decoder.TryDecodeU8Archive(archive);
            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : "");
            Assert.Equal(entries.Count, result.Value.Files.Count);
            foreach (var pair in entries) Assert.Equal(pair.Value, result.Value.Files[pair.Key]);
        }
    }

    [Fact]
    public void NintendoDotRootRoundTripsAndMatchesBaselineWithoutFalseDeletions()
    {
        var files = new Dictionary<string, byte[]> { ["./dir/a.bin"] = [1, 2] };
        var raw = U8ArchiveBuilder.BuildYaz0(files);
        Assert.Equal(files["./dir/a.bin"], decoder.TryDecodeU8Archive(raw).Value.Files["./dir/a.bin"]);
        var result = Converter.AnalyzeAgainstBaseline(SzsBaseline(files), "Race.szs", raw);
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Entries);
    }

    [Fact]
    public void SzsAnalysisPreservesChangesDeletionsOrderAndMessages()
    {
        var baseline = SzsBaseline(new() { ["same.bin"] = [1], ["dir/change.bin"] = [2], ["gone.bin"] = [3] });
        var result = Converter.AnalyzeAgainstBaseline(baseline, "Race.szs", U8ArchiveBuilder.Build(
            new Dictionary<string, byte[]> { ["same.bin"] = [1], ["dir/change.bin"] = [4], ["new.bin"] = [5] }));
        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { "gone.bin.delete.Race", "new.bin.Race", "[dir]change.bin.Race" }, result.Value.Entries.Select(e => e.ExportPath));
        Assert.Equal(new byte[] { 4 }, result.Value.Entries[2].Bytes);
        Assert.Empty(result.Value.Entries[0].Bytes);
        Assert.Equal("text.modified_archive_member", result.Value.Entries[2].Detail.Key);
        Assert.Equal("text.new_archive_member", result.Value.Entries[1].Detail.Key);
        Assert.Empty(result.Value.Skipped);
    }

    [Fact]
    public void UnchangedAndRawBaselinesKeepExistingSemantics()
    {
        var members = new Dictionary<string, byte[]> { ["same.bin"] = [1] };
        var bytes = U8ArchiveBuilder.Build(members);
        var baseline = SzsBaseline(members);
        Assert.Equal(0, Converter.EstimateDifference(baseline, bytes));
        Assert.Empty(Converter.AnalyzeAgainstBaseline(baseline, "Race.szs", bytes).Value.Entries);
        baseline.Mode = "raw";
        var changed = new byte[] { 4, 5 };
        var analysis = Converter.AnalyzeAgainstBaseline(baseline, "Different.szs", changed).Value;
        Assert.Equal(changed, Assert.Single(analysis.Entries).Bytes);
        Assert.Equal("Race.szs", analysis.Entries[0].ExportPath);
        var warning = Assert.Single(analysis.Warnings, m => m.Key == "warning.file_name_differs_from_archive_tag");
        Assert.Equal("Race", Assert.Single(warning.Arguments));
    }

    [Fact]
    public void UnsupportedLooseOverridesRemainSkipped()
    {
        var result = Converter.AnalyzeAgainstBaseline(SzsBaseline(new()), "Race.szs",
            U8ArchiveBuilder.Build(new Dictionary<string, byte[]> { ["course.kcl"] = [1] })).Value;
        Assert.Empty(result.Entries);
        Assert.Equal("warning.unsupported_loose_override_extension", Assert.Single(result.Skipped).Key);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("range")]
    [InlineData("count")]
    [InlineData("type")]
    [InlineData("path")]
    public void MalformedU8CannotTurnMissingMembersIntoDeletionPatches(string corruption)
    {
        var members = new Dictionary<string, byte[]> { ["ok.bin"] = [1] };
        var raw = U8ArchiveBuilder.Build(members);
        var root = (int)BigEndianBinaryHelper.BufferToUint32(raw, 4);
        var node = root + 12;
        switch (corruption)
        {
            case "name": raw[node + 1] = 0xff; break;
            case "range": Put(raw, node + 4, raw.Length + 1); break;
            case "count": Put(raw, root + 8, int.MaxValue); break;
            case "type": raw[node] = 2; break;
            case "path":
                var name = root + 24 + (int)(BigEndianBinaryHelper.BufferToUint32(raw, node) & 0xffffff);
                raw[name] = (byte)'/'; break;
        }
        var result = Converter.AnalyzeAgainstBaseline(SzsBaseline(members), "broken.szs", raw);
        Assert.True(result.IsFailure);
        Assert.Contains("U8", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../a")]
    [InlineData("/a")]
    [InlineData("C:/a")]
    [InlineData("a\\b")]
    [InlineData("a/./b")]
    [InlineData("a//b")]
    [InlineData("a/ .. /b")]
    public void UnsafePathsAreRejectedBeforeBuildingOrExporting(string path)
    {
        Assert.Throws<InvalidDataException>(() => U8ArchiveBuilder.Build(new Dictionary<string, byte[]> { [path] = [1] }));
        var baseline = SzsBaseline(new() { [path] = [1] });
        Assert.True(Converter.AnalyzeAgainstBaseline(baseline, "Race.szs", U8ArchiveBuilder.Build(new Dictionary<string, byte[]>())).IsFailure);
    }

    [Fact]
    public void TruncatedAndImpossibleYaz0FailBeforeAllocation()
    {
        Assert.True(decoder.DecompressYaz0IfNeeded("Yaz0"u8.ToArray()).IsFailure);
        var bytes = new byte[16]; "Yaz0"u8.CopyTo(bytes); Put(bytes, 4, int.MaxValue);
        Assert.True(decoder.DecompressYaz0IfNeeded(bytes).IsFailure);
        Assert.True(decoder.TryDecodeU8Archive([0x55, 0xaa, 0x38, 0x2d]).IsFailure);
    }

    [Fact]
    public void BrsarExportsSupportedSoundAndAlignedWaveWithStableOrder()
    {
        var bytes = BrsarFixture();
        var result = BrsarPatchConverter.AnalyzeAgainstBaseline(BrsarBaseline(), "revo_kart.brsar", bytes);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : "");
        var entry = Assert.Single(result.Value.Entries);
        Assert.Equal("0.brwsd", entry.ExportPath);
        Assert.Equal(bytes[0x200..0x240], entry.Bytes);
        Assert.Equal("text.brsar_entry_with_rwar", entry.Detail.Key);
        Assert.Equal(1, BrsarPatchConverter.EstimateDifference(BrsarBaseline(), bytes));
        var same = BrsarBaseline();
        same.Entries = Rows(new object[] { 0, "s", "RWSD", 64, Hash(entry.Bytes) });
        Assert.Empty(BrsarPatchConverter.AnalyzeAgainstBaseline(same, "revo_kart.brsar", bytes).Value.Entries);
        Assert.Equal(0, BrsarPatchConverter.EstimateDifference(same, bytes));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BrsarUnsupportedAndExternalSoundsRemainExplicitlySkipped(bool external)
    {
        var bytes = BrsarFixture();
        if (external) { Put(bytes, 0xcc + 4, 0x1e0); Encoding.ASCII.GetBytes("external.brstm\0").CopyTo(bytes, 0x1e0); }
        else "RWAV"u8.CopyTo(bytes.AsSpan(0x200));
        var result = BrsarPatchConverter.AnalyzeAgainstBaseline(BrsarBaseline(), "revo_kart.brsar", bytes).Value;
        Assert.Empty(result.Entries);
        Assert.Equal(external ? "warning.brsar_file_id_external" : "warning.brsar_file_id_unsupported_magic", Assert.Single(result.Skipped).Key);
    }

    [Theory]
    [InlineData(0xc0, -1)]
    [InlineData(0xc4, -1)]
    [InlineData(0x80, -1)]
    [InlineData(0x80, int.MaxValue)]
    [InlineData(0x88, 0)]
    [InlineData(0x88, int.MaxValue)]
    [InlineData(0x18, int.MaxValue)]
    public void InvalidBrsarReferencesFailWithoutPartialAnalysis(int offset, int value)
    {
        var bytes = BrsarFixture(); Put(bytes, offset, value);
        var result = BrsarPatchConverter.AnalyzeAgainstBaseline(BrsarBaseline(), "broken.brsar", bytes);
        Assert.True(result.IsFailure);
        Assert.Contains("broken.brsar", result.Error.Message);
        Assert.NotNull(result.Error.Exception);
    }

    private static BaselineEntry SzsBaseline(Dictionary<string, byte[]> files)
    {
        var bytes = U8ArchiveBuilder.Build(new Dictionary<string, byte[]>());
        return new() { Kind = "szs", ArchiveTag = "Race", RelativePath = "Scene/UI/Race.szs", Mode = "tagged-archive",
            WholeFileSize = bytes.Length, WholeFileHash = "different",
            Members = Rows(files.Select(f => new object[] { f.Key, f.Value.Length, Hash(f.Value) }).ToArray()) };
    }
    private static BaselineEntry BrsarBaseline() => new() { Kind = "brsar", Entries = Rows(new object[] { 0, "s", "RWSD", 64, "different" }) };
    private static List<object[]> Rows(params object[][] rows) => JsonSerializer.Deserialize<List<object[]>>(JsonSerializer.Serialize(rows))!;
    private static string Hash(byte[] bytes)
    {
        unchecked { uint low = 0x811c9dc5, high = 0x9e3779b1; foreach (var b in bytes) { low = (low ^ b) * 0x01000193; high = (high ^ b) * 0x85ebca6b; } return $"{low:x8}{high:x8}"; }
    }
    private static void Put(byte[] bytes, int offset, int value) => BigEndianBinaryHelper.WriteUInt32BigEndian(bytes, offset, unchecked((uint)value));
    private static byte[] BrsarFixture()
    {
        var b = new byte[0x240]; "RSAR"u8.CopyTo(b); Put(b, 0x18, 0x40);
        void Ref(int at, int target) => Put(b, at + 4, target);
        void Table(int at, int target) { Put(b, at, 1); Ref(at + 4, target); }
        Ref(0x60, 0x80); Ref(0x68, 0x90); Table(0x80, 0xc0); Table(0x90, 0x100);
        Put(b, 0xc0, 0x20); Put(b, 0xc4, 0x20); Ref(0xd4, 0x160);
        Put(b, 0x110, 0x200); Put(b, 0x118, 0x220); Ref(0x120, 0x140);
        Table(0x140, 0x1a0); Table(0x160, 0x180);
        "RWSD"u8.CopyTo(b.AsSpan(0x200)); Put(b, 0x208, 0x20);
        "RWAR"u8.CopyTo(b.AsSpan(0x220)); Put(b, 0x228, 0x20);
        return b;
    }
}
