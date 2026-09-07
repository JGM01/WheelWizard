using WheelWizard.Core.Patches;

namespace WheelWizard.Features.Patches;

public static class PatchConversionText
{
    // These details were literal strings before extraction; preserve their displayed text.
    public static string Render(ConversionMessage message) => message.Key switch
    {
        "patch.detail.deleted_member" => "Deleted archive member",
        "patch.detail.unresolved" => (string)message.Arguments[0]!,
        _ => t(message.Key, message.Arguments),
    };
}
