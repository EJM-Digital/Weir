using Weir.Core.Text;

namespace Weir.Core.Auth;

/// <summary>Every sentence the server says about recovery codes, in one place so they can be kept in step with Deluno's wording.</summary>
public static class RecoveryCodeMessages
{
    public const string NewSetMade = "New recovery codes made. Save them now: Weir shows them only once, and the old codes no longer work.";
    public const string SetConfirmed = "Thanks. Your recovery codes are marked as saved.";
    public const string CodeNotRight = "That recovery code isn't right, or it has been used already. Check it and try again.";
    public const string TooManyFromThisAddress = "Too many recovery attempts from this address. Try again later.";
    public const string TooManyWrongCodes = "Too many wrong recovery codes. Wait a few minutes, then try again.";
    public const string ResetTimedOut = "This password reset has timed out. Use another recovery code to start again.";
    public const string PasswordSet = "Password changed. Sign in with your new password.";

    public const string ActivityTitle = "Sign-in recovered with a recovery code";

    public const string AlertTitle = "Sign-in recovered with a recovery code";

    public static string ActivityDetail(string username, int codesLeft) => $"{username}. {CodesLeft(codesLeft)}.";

    public static string AlertDetail(string username, int codesLeft) =>
        $"A recovery code was used to get back into Weir as {username}. {CodesLeft(codesLeft)}. " +
        "If this was not you, make a new set of recovery codes under System, Security, as soon as you can sign in.";

    private static string CodesLeft(int count) => $"{Plural.Of(count, "recovery code")} left";
}
