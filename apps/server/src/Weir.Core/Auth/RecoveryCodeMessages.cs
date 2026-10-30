namespace Weir.Core.Auth;

/// <summary>Every sentence the server says about recovery codes, in one place so they can be kept in step with Deluno's wording.</summary>
public static class RecoveryCodeMessages
{
    public const string NewSetMade = "New recovery codes made. Save them now: Weir shows them only once, and the old codes no longer work.";
    public const string SetConfirmed = "Thanks. Your recovery codes are marked as saved.";
    public const string CodeNotRight = "That did not work. Check it and try again.";
    public const string TooManyTries = "Too many tries. Wait 10 minutes and try again.";
    public const string ResetTimedOut = "This password reset has timed out. Use another recovery code to start again.";
    public const string PasswordSet = "Your password is changed.";

    /// <summary>The Activity line for a recovery, and the body of the alert that goes with it.</summary>
    public const string Reset = "Password reset with a recovery code. Every signed-in session was ended.";

    public const string AlertTitle = "Weir password reset";

    /// <summary>The Activity line's detail: whose account, and how many codes it has left.</summary>
    public static string ActivityDetail(string username, int codesLeft) => $"{username}. {codesLeft} of {RecoveryCodeRules.SetSize} recovery codes left.";
}
