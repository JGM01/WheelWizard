using Serilog;
using WheelWizard.Views.Popups.Generic;

namespace WheelWizard.Shared.MessageTranslations;

public static class MessageTranslationHelper
{
    /// <summary>
    /// Returns the translation related to this message enum
    /// </summary>
    /// <returns>(Title, additional information)</returns>
    public static (string, string?) GetTranslationText(MessageTranslation msg)
    {
        return msg switch
        {
            #region Successes

            MessageTranslation.Success_StanderdSuccess => ("Success", "Completed successfully!"),
            MessageTranslation.Success_PathSettingsSaved => (
                t("message_success.settings_saved.title"),
                t("message_success.settings_saved.title")
            ),

            #endregion

            #region Warnings

            MessageTranslation.Warning_StanderdWarning => ("Warning", "Something went wrong!"),
            MessageTranslation.Warning_InvalidPathSettings => (
                t("message_warning.invalid_paths.title"),
                t("message_warning.invalid_paths.extra")
            ),
            MessageTranslation.Warning_UnkownRendererSelected => ("Unknown renderer selected", "Unknown renderer selected: {$1}"),
            MessageTranslation.Warning_CouldNotFindRoom => (
                "Couldn't find the room",
                "Whoops, could not find the room that this player is supposedly playing in"
            ),
            MessageTranslation.Warning_CantDeleteFavMii => (
                t("message_warning.cannot_delete_fav_mii.title"),
                t("message_warning.cannot_delete_fav_mii.extra")
            ),

            MessageTranslation.Warning_CantViewMod_SomethingWrong => (
                t("message_warning.cant_view_mod.title"),
                t("message_warning.cant_view_mod.extra.something_else")
            ),
            MessageTranslation.Warning_CantViewMod_NotFromBrowser => (
                t("message_warning.cant_view_mod.title"),
                t("message_warning.cant_view_mod.extra.not_from_browser")
            ),
            MessageTranslation.Warning_NoMiisFound => ("No Miis Found", "There are no other Miis available to select."),
            MessageTranslation.Warning_DolphinNotFound => (
                t("message_warning.dolphin_not_found.title"),
                t("message_warning.dolphin_not_found.extra")
            ),
            MessageTranslation.Warning_DolphinToolSelected => (
                t("message_warning.dolphin_tool_selected.title"),
                t("message_warning.dolphin_tool_selected.extra")
            ),
            MessageTranslation.Warning_ModNameCantEmpty => (
                t("message_warning.mod_name_empty.title"),
                t("message_warning.mod_name_empty.extra")
            ),
            MessageTranslation.Warning_ModNameInvalid => (
                t("message_warning.mod_name_invalid.title"),
                t("message_warning.mod_name_invalid.extra")
            ),
            MessageTranslation.Warning_UnableToDownloadMod_Files => (
                t("message_warning.unable_download_mod.title"),
                t("message_warning.unable_download_mod.extra")
            ),

            #endregion

            #region Errors

            MessageTranslation.Error_StanderdError => ("Standard Error", "Something went wrong!"),
            MessageTranslation.Error_ModDownloadFailed => (
                t("message_error.mod_download_fail.title"),
                t("message_error.mod_download_fail.extra")
            ),
            MessageTranslation.Error_FailedCopyMii => ("Failed to copy Mii", "{$1}"),
            MessageTranslation.Error_MiiDBAlreadyExists => (t("message_error.failed_create_mii_db.title"), "Database already exists."),
            MessageTranslation.Error_UpdateMiiDb_InvalidClId => ("Invalid Client ID.", "The client ID attached to this Mii is invalid."),
            MessageTranslation.Error_UpdateMiiDb_BlockSizeInvalid => ("Mii block size invalid.", null),
            MessageTranslation.Error_UpdateMiiDb_NoBlockFound => ("Mii block not found.", null),
            MessageTranslation.Error_UpdateMiiDb_MiiNotFound => ("Mii not found", null),
            MessageTranslation.Error_UpdateMiiDb_InvalidMac => ("Invalid MAC Address", "The MAC attached to this Mii is invalid."),
            MessageTranslation.Error_UpdateMiiDb_RFLdbNotFound => ("RFL_DB.dat not found", "The RFL_DB.dat file could not be found."),
            MessageTranslation.Error_UpdateMiiDb_CorruptDb => (
                "Corrupt Mii Database",
                "Corrupt Mii database (bad CRC 0x{$1}, expected 0x{$2})."
            ),
            MessageTranslation.Error_MiiSerializer_MiiNotNull => ("Mii cannot be null", null),
            MessageTranslation.Error_MiiSerializer_MiiId0 => ("Mii ID cannot be 0", null),
            MessageTranslation.Error_MiiSerializer_MiiDataLength => ("Invalid Mii data length.", null),
            MessageTranslation.Error_MiiSerializer_MiiDataEmpty => ("Mii data is empty.", null),
            MessageTranslation.Error_MiiSerializer_InvalidMiiData => ("Invalid Mii data", "The Mii '{$1}' is invalid."),

            MessageTranslation.Error_MiiEditor_CantOpenEditor => ("Cant open Mii Editor", "{$1}"),

            MessageTranslation.Error_FailedInstallDolphin => (
                t("message_error.failed_install_dolphin.title"),
                t("message_error.failed_install_dolphin.extra")
            ),

            #endregion

            _ => ("Message", $"Unknown translation for: {msg.ToString()}"),
        };
    }

    #region Base Stuff

    /// <summary>
    ///  Shows a message box with the given message enum.
    /// </summary>
    public static void ShowMessage(MessageTranslation msg, object[]? titleReplacements = null, object[]? extraReplacements = null) =>
        CreateMessageBox(msg, titleReplacements, extraReplacements).Show();

    public static void ShowMessage(OperationError error)
    {
        LogOperationError(error);
        CreateMessageBox(error).Show();
    }

    public static Task AwaitMessageAsync(MessageTranslation msg, object[]? titleReplacements = null, object[]? extraReplacements = null) =>
        CreateMessageBox(msg, titleReplacements, extraReplacements).ShowDialog();

    public static Task AwaitMessageAsync(OperationError error)
    {
        LogOperationError(error);
        return CreateMessageBox(error).ShowDialog();
    }

    private static void LogOperationError(OperationError error)
    {
        if (error.Exception != null)
        {
            Log.Error(error.Exception, "Showing operation error: {Message}", error.Message);
            return;
        }

        Log.Warning("Showing operation error: {Message}", error.Message);
    }

    private static MessageBoxWindow CreateMessageBox(
        MessageTranslation msg,
        object[]? titleReplacements = null,
        object[]? extraReplacements = null
    )
    {
        var (title, extraText) = GetTranslationText(msg);
        var type =
            (int)msg < 1000 ? MessageBoxWindow.MessageType.Message
            : (int)msg < 3000 ? MessageBoxWindow.MessageType.Warning
            : MessageBoxWindow.MessageType.Error;
        var box = new MessageBoxWindow().SetMessageType(type).SetTitleText(tFormat(title, titleReplacements ?? []));
        if (extraText == null)
            box.SetInfoText(tFormat(title, titleReplacements ?? []));
        else
            box.SetInfoText(tFormat(extraText, extraReplacements ?? []));

        if ((int)msg >= 2000)
            box.SetTag($"{(int)msg}");

        return box;
    }

    private static MessageBoxWindow CreateMessageBox(OperationError error)
    {
        if (error.MessageTranslation != null)
            return CreateMessageBox((MessageTranslation)error.MessageTranslation, error.TitleReplacements, error.ExtraReplacements);

        return new MessageBoxWindow()
            .SetMessageType(MessageBoxWindow.MessageType.Error)
            .SetTag("Unk")
            .SetTitleText(t("message_error.generic_error.title"))
            .SetInfoText(error.Message);
    }

    #endregion
}
