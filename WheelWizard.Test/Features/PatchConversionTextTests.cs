using WheelWizard.Core.Patches;
using WheelWizard.Features.Patches;
using WheelWizard.Localization;

namespace WheelWizard.Test.Features;

public sealed class PatchConversionTextTests
{
    [Theory]
    [InlineData("warning.file_name_differs_from_archive_tag", "Race")]
    [InlineData("warning.unsupported_loose_override_extension", "course.kcl")]
    [InlineData("warning.brsar_file_id_external", 7)]
    [InlineData("warning.brsar_external_count", 2)]
    public void MessagesRetainExistingTranslationAndArguments(string key, object argument)
    {
        Assert.Equal(TranslationFunctions.t(key, argument), PatchConversionText.Render(new(key, argument)));
    }

    [Fact]
    public void ConversionSummaryContractRemainsStringsAndLiteralDetailsArePreserved()
    {
        var result = new ModPatchConversionResult { ConvertedFileCount = 2, WrittenPatchCount = 3,
            Warnings = [PatchConversionText.Render(new("warning.szs_matches_baseline"))], Skipped = ["file: reason"] };
        Assert.Equal(2, result.ConvertedFileCount);
        Assert.Equal(3, result.WrittenPatchCount);
        Assert.Equal(TranslationFunctions.t("warning.szs_matches_baseline"), Assert.Single(result.Warnings));
        Assert.Equal("Deleted archive member", PatchConversionText.Render(new("patch.detail.deleted_member")));
    }
}
