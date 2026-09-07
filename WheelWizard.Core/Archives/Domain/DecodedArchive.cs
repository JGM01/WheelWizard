namespace WheelWizard.Core.Archives;

public sealed record DecodedArchive(IReadOnlyDictionary<string, byte[]> Files);
