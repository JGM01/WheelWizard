namespace WheelWizard.Core;

// Section and key names of the runtime Config.toml, shared by RuntimeConfiguration
// and every consumer of it (the native product workflow and each frontend's settings).
public static class RuntimeConfigKeys
{
    public const string Paths = "paths";
    public const string Network = "network";
    public const string Audio = "audio";
    public const string Video = "video";

    public const string DvdRoot = "dvd_root";
    public const string RetroRewindRoot = "retro_rewind_root";
    public const string OverlayRoots = "overlay_roots";
    public const string NetworkEnabled = "enabled";
    public const string Volume = "volume";
    public const string ResolutionMultiplier = "resolution_multiplier";
}
