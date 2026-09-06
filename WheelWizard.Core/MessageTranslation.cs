namespace WheelWizard.Core;

// MAKE SURE EVERY ENUM VALUE HAS A VALUE
// 0xxx = Successes
// 1xxx = Warnings (without tag)
// 2xxx = Warnings (with tag)
// 3xxx = Errors
// ANY OTHER VALUE BELOW 1000 OR ABOVE 3999 is not valid

// When determining if something is an error or a warning, keep in mind the following questions:
// - Is it the user's fault?                                        (e.g. the user entering a wrong path, that's their fault)
// - Is this something that we as WhWz can do anything about?       (e.g. we can't do anything about internet related issues, but a missing file that we should have created is our fault)
// If any of the questions above can be answered with "yes", then it is a warning, otherwise it is an error.

// Note that you can safely re-organize the actual enum numbers. They might be serialized in the future, or screenshots might be taken from the numbers. So those will then be outdated, but thats not a problem
// as longs as wheel wizard it-self never reads and converts the numbers back to the enum values.
public enum MessageTranslation
{
    #region Successes

    Success_StanderdSuccess = 0000,
    Success_PathSettingsSaved = 0001,

    #endregion

    #region Warnings

    // Warning starting with 1 have NO error code displayed, once starting with 2 DO HAVE an error code displayed, like the tag
    // If warning makes sense on its own what the user did wrong, then no tag is needed (1xxx)
    Warning_StanderdWarning = 2000,
    Warning_InvalidPathSettings = 1001,
    Warning_UnkownRendererSelected = 2002,
    Warning_CouldNotFindRoom = 2003,
    Warning_CantDeleteFavMii = 1004,
    Warning_CantViewMod_NotFromBrowser = 1005,
    Warning_CantViewMod_SomethingWrong = 2006,
    Warning_NoMiisFound = 1007,
    Warning_DolphinNotFound = 2008,
    Warning_ModNameCantEmpty = 1009,
    Warning_ModNameInvalid = 1010,
    Warning_UnableToDownloadMod_Files = 1011,
    Warning_DolphinToolSelected = 1012,

    #endregion

    #region Errors

    // 30xx = random errors
    // 31xx = Mii (related) Errors
    // - 310x = Mii Repository/DB Error
    // - 311x = Mii Serializer Error
    // - 312x = Mii Editor
    // 32xx = External program Errors
    // - 320x = Dolphin related Errors
    Error_StanderdError = 3000,
    Error_ModDownloadFailed = 3001,

    Error_MiiDBAlreadyExists = 3100,
    Error_UpdateMiiDb_InvalidClId = 3101,
    Error_UpdateMiiDb_BlockSizeInvalid = 3102,
    Error_UpdateMiiDb_NoBlockFound = 3103,
    Error_UpdateMiiDb_MiiNotFound = 3104,
    Error_UpdateMiiDb_InvalidMac = 3105,
    Error_UpdateMiiDb_RFLdbNotFound = 3106,
    Error_UpdateMiiDb_CorruptDb = 3107,

    Error_MiiSerializer_MiiNotNull = 3110,
    Error_MiiSerializer_MiiId0 = 3111,
    Error_MiiSerializer_MiiDataLength = 3112,
    Error_MiiSerializer_MiiDataEmpty = 3113,
    Error_MiiSerializer_InvalidMiiData = 3114,
    Error_MiiEditor_CantOpenEditor = 3120,

    Error_FailedCopyMii = 3200,
    Error_FailedInstallDolphin = 3201,

    #endregion
}

