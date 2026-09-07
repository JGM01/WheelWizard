namespace WheelWizard.Features.Patches;

public sealed class ModPatchConversionResult
{
    public int ConvertedFileCount { get; init; }
    public int WrittenPatchCount { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<string> Skipped { get; init; } = [];
}
