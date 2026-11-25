namespace ZennoVibe.MailClient.Core;

internal enum AuthMode
{
    XOAUTH2 = 0,
    AppPassword = 1
}

/// <summary>How the account line looked in the import file, so a later export can write it back.</summary>
internal enum ImportedLineKind
{
    /// <summary>The column order from Settings.</summary>
    SettingsFormat = 0,

    /// <summary><c>mail:password</c></summary>
    MailAndPassword = 1,

    /// <summary><c>mail:password:phone:app_password</c></summary>
    MailPasswordPhoneApp = 2
}
