namespace WheelWizard.Core.Recomp;

// Build inputs the frontends send with every request; tool paths may be left blank
// and are resolved to installed tools by the workflow.
public record SetupInput(
    string Wbfs = "",
    string Workspace = "",
    string Cmake = "",
    string Ninja = "",
    string Nodtool = "",
    string Translator = ""
);

public record ProductStatus(string Id, string Name, bool Ready, string Detail);

public sealed record Product(string Id, string Name, string App);

// The product catalog a native recomp distribution can build and launch.
public static class RecompProducts
{
    public static readonly Product Base = new("base", "Mario Kart Wii", "WiiCompiled");
    public static readonly Product RetroRewind = new("retro-rewind", "Retro Rewind", "RetroRewind");
    public static readonly Product[] All = [Base, RetroRewind];

    public static Product ById(string id) =>
        All.FirstOrDefault(p => p.Id == id) ?? throw new ArgumentException("Unknown product", nameof(id));
}

// Fingerprint of the inputs a published product was built from. WbfsHash only applies
// to base; PayloadHash hashes the Retro Rewind Code.pul for retro-rewind and is empty
// otherwise. Mode is implied by the product id.
public sealed record ProductInput(
    string Wbfs,
    string WbfsHash,
    string Workspace,
    string Revision,
    string PayloadHash
);

// Persisted next to each published product; readiness compares Input to a freshly
// computed ProductInput, ignoring the BuiltAt provenance timestamp.
public sealed record ProductReceipt(string ProductId, ProductInput Input, DateTimeOffset BuiltAt);
