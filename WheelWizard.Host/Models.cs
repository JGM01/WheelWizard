using WheelWizard.Core;
using WheelWizard.Core.Recomp;

namespace WheelWizard.Host;

public record Request(
    int Version,
    string Id,
    string Command,
    SetupInput? Setup = null,
    string? Product = null,
    RuntimeSettings? Settings = null,
    string? ModTitle = null,
    string? ArchivePath = null,
    bool? Enabled = null,
    int? Direction = null
);

public record HostEvent(int Version, string Id, string Kind, object? Data = null, string? Outcome = null, string? Error = null);
