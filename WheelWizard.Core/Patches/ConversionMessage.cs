namespace WheelWizard.Core.Patches;

/// <summary>Presentation data only: frontends resolve the key using their own localization.</summary>
public sealed record ConversionMessage(string Key, params object?[] Arguments);
