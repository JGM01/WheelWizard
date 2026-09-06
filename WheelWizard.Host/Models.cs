namespace WheelWizard.Host;

public record SetupInput(
    string Wbfs = "",
    string Workspace = "",
    string Cmake = "",
    string Ninja = "",
    string Nodtool = "",
    string Translator = ""
);

public record ProductStatus(string Id, string Name, bool Ready, string Detail);

public record BuildIdentity(string Wbfs, string WbfsHash, string Workspace, string Revision, string CodeHash, string Mode);

public record BuildReceipt(string Product, BuildIdentity Input);

public record Request(
    int Version,
    string Id,
    string Command,
    SetupInput? Setup = null,
    string? Product = null,
    WheelWizard.Core.RuntimeSettings? Settings = null
);

public record HostEvent(int Version, string Id, string Kind, object? Data = null, string? Outcome = null, string? Error = null);
