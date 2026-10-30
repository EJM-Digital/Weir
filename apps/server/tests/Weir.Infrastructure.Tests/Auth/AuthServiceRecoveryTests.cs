using Weir.Core.Auth;
using Weir.Core.Json;
using Weir.Core.Security;
using Weir.Infrastructure.Auth;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Auth;

/// <summary>Recovery codes end to end in the service: made hashed at the product's cost, single-use, voided by a new set, and the way back in they give.</summary>
public sealed class AuthServiceRecoveryTests : IDisposable
{
    private const string Password = "test-password-strong";
    private const string Secret = "recovery-test-secret-recovery-test-secret";

    private readonly StoreFixture _fixture = new();
    private readonly RecoveryCodeStore _codes = new();

    public void Dispose() => _fixture.Dispose();

    private async Task<UserRecord> SeedAsync(string name = "alice")
    {
        var hash = CheapPasswordHash.For(Password);
        var id = await _fixture.WithUnitOfWork(uow => _fixture.Users.InsertUserAsync(uow, name, hash, "admin", true));
        return new UserRecord(id, name, hash, "admin", true);
    }

    /// <summary>A set stored the way the product stores one, but hashed at the cheapest cost so the test does not spend a second on each code.</summary>
    private async Task<IReadOnlyList<string>> SeedSetAsync(UserRecord user)
    {
        var set = RecoveryCodeRules.NewSet();
        await _fixture.WithUnitOfWork(async uow =>
        {
            await _codes.ReplaceSetAsync(uow, user.Id, [.. set.Select(code => (code[..5], CheapPasswordHash.For(code)))], _fixture.Auth.Now());
            return 0;
        });
        return set;
    }

    private Task<RecoveredAccount?> RedeemAsync(string typed) => _fixture.WithUnitOfWork(uow => _fixture.Auth.RedeemRecoveryCodeAsync(uow, typed));

    private Task<RecoveryCodeStatus> StatusAsync(UserRecord user) => _fixture.WithUnitOfWork(uow => _fixture.Auth.RecoveryCodeStatusAsync(uow, user.Id));

    [Fact]
    public async Task A_new_set_is_ten_codes_kept_only_as_hashes_and_a_newer_set_voids_the_older()
    {
        var user = await SeedAsync();

        var first = await _fixture.WithUnitOfWork(uow => _fixture.Auth.MakeRecoveryCodesAsync(uow, user.Id));
        var second = await _fixture.WithUnitOfWork(uow => _fixture.Auth.MakeRecoveryCodesAsync(uow, user.Id));

        Assert.Equal(RecoveryCodeRules.SetSize, first.Count);
        Assert.Equal(RecoveryCodeRules.SetSize, second.Count);
        Assert.Equal(RecoveryCodeRules.SetSize, await _fixture.Scalar("SELECT count(*) FROM recovery_codes"));
        Assert.Equal(RecoveryCodeRules.SetSize, await _fixture.Scalar("SELECT count(*) FROM recovery_codes WHERE code_hash LIKE '$argon2id$%' AND created_at IS NOT NULL"));
        var stored = await _fixture.ScalarText("SELECT group_concat(lookup || code_hash, ' ') FROM recovery_codes");
        Assert.All(second, code => Assert.DoesNotContain(code[6..], stored!, StringComparison.Ordinal));

        Assert.Null(await RedeemAsync(first[0]));
        var redeemed = await RedeemAsync(second[0]);
        Assert.Equal(user.Id, redeemed!.User.Id);
    }

    [Fact]
    public async Task A_code_works_once_ends_every_session_and_says_how_many_codes_are_left()
    {
        var user = await SeedAsync();
        var set = await SeedSetAsync(user);
        await _fixture.WithUnitOfWork(uow => _fixture.Auth.CreateSessionAsync(uow, user, false, "one"));
        await _fixture.WithUnitOfWork(uow => _fixture.Auth.CreateSessionAsync(uow, user, true, "two"));

        var redeemed = await RedeemAsync(set[3].ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal));

        Assert.Equal("alice", redeemed!.User.Username);
        Assert.Equal(RecoveryCodeRules.SetSize - 1, redeemed.CodesLeft);
        Assert.Equal(0, await _fixture.Scalar("SELECT count(*) FROM user_sessions"));
        Assert.Null(await RedeemAsync(set[3]));
        Assert.Equal(RecoveryCodeRules.SetSize - 1, (await StatusAsync(user)).Remaining);
    }

    [Fact]
    public async Task A_wrong_code_a_missing_code_and_something_that_is_no_code_are_all_refused_and_spend_nothing()
    {
        var user = await SeedAsync();
        var set = await SeedSetAsync(user);
        var sameLookupWrongRest = set[0][..6] + "AAAAA-AAAAA-AAAAA";
        var unknownLookup = set.Any(code => code.StartsWith("ZZZZZ", StringComparison.Ordinal)) ? "YYYYY-AAAAA-AAAAA-AAAAA" : "ZZZZZ-AAAAA-AAAAA-AAAAA";

        Assert.Null(await RedeemAsync(sameLookupWrongRest));
        Assert.Null(await RedeemAsync(unknownLookup));
        Assert.Null(await RedeemAsync("not a code"));
        Assert.Null(await RedeemAsync(string.Empty));

        Assert.Equal(RecoveryCodeRules.SetSize, (await StatusAsync(user)).Remaining);
        Assert.NotNull(await RedeemAsync(set[0]));
    }

    [Fact]
    public async Task A_code_spent_by_another_request_while_this_one_was_being_checked_is_not_spent_twice()
    {
        var user = await SeedAsync();
        var set = await SeedSetAsync(user);

        var second = await _fixture.WithUnitOfWork(uow => _fixture.Auth.RedeemRecoveryCodeAsync(
            uow,
            set[0],
            afterVerified: async () => Assert.NotNull(await RedeemAsync(set[0]))));

        Assert.Null(second);
        Assert.Equal(RecoveryCodeRules.SetSize - 1, (await StatusAsync(user)).Remaining);
    }

    [Fact]
    public async Task The_status_tells_an_account_with_no_codes_a_set_not_yet_saved_and_a_saved_one()
    {
        var user = await SeedAsync();
        Assert.Equal(new RecoveryCodeStatus(0, false), await StatusAsync(user));

        var set = await SeedSetAsync(user);
        Assert.Equal(new RecoveryCodeStatus(RecoveryCodeRules.SetSize, false), await StatusAsync(user));

        Assert.Equal(RecoveryCodeRules.SetSize, await _fixture.WithUnitOfWork(uow => _fixture.Auth.ConfirmRecoveryCodesAsync(uow, user.Id)));
        Assert.Equal(new RecoveryCodeStatus(RecoveryCodeRules.SetSize, true), await StatusAsync(user));

        await RedeemAsync(set[0]);
        Assert.Equal(new RecoveryCodeStatus(RecoveryCodeRules.SetSize - 1, true), await StatusAsync(user));

        await SeedSetAsync(user);
        Assert.Equal(new RecoveryCodeStatus(RecoveryCodeRules.SetSize, false), await StatusAsync(user));
    }

    [Fact]
    public async Task The_token_a_spent_code_gives_sets_the_new_password_once_and_the_old_one_stops_working()
    {
        var user = await SeedAsync();
        var set = await SeedSetAsync(user);
        var redeemed = (await RedeemAsync(set[0]))!;
        var token = _fixture.Auth.IssueRecoveryResetToken(Secret, redeemed.User);

        var username = await _fixture.WithUnitOfWork(uow => _fixture.Auth.ResetPasswordAfterRecoveryAsync(uow, Secret, token, "a-new-strong-password"));

        Assert.Equal("alice", username);
        Assert.NotNull(await _fixture.WithUnitOfWork(uow => _fixture.Auth.AuthenticateAsync(uow, "alice", "a-new-strong-password")));
        Assert.Null(await _fixture.WithUnitOfWork(uow => _fixture.Auth.AuthenticateAsync(uow, "alice", Password)));
        var again = await Assert.ThrowsAsync<WireValueException>(
            () => _fixture.WithUnitOfWork(uow => _fixture.Auth.ResetPasswordAfterRecoveryAsync(uow, Secret, token, "another-strong-password")));
        Assert.Equal(RecoveryCodeMessages.ResetTimedOut, again.Message);
    }

    [Fact]
    public async Task A_token_that_is_not_ours_or_names_no_account_or_a_password_that_is_too_weak_is_refused()
    {
        var user = await SeedAsync();
        var token = _fixture.Auth.IssueRecoveryResetToken(Secret, user);
        var nobody = _fixture.Auth.IssueRecoveryResetToken(Secret, user with { Id = 9999 });

        async Task<string> Refusal(string secret, string issued, string password) =>
            (await Assert.ThrowsAsync<WireValueException>(
                () => _fixture.WithUnitOfWork(uow => _fixture.Auth.ResetPasswordAfterRecoveryAsync(uow, secret, issued, password)))).Message;

        Assert.Equal(RecoveryCodeMessages.ResetTimedOut, await Refusal(Secret, "garbage", "a-new-strong-password"));
        Assert.Equal(RecoveryCodeMessages.ResetTimedOut, await Refusal("another-secret-another-secret-another", token, "a-new-strong-password"));
        Assert.Equal(RecoveryCodeMessages.ResetTimedOut, await Refusal(Secret, nobody, "a-new-strong-password"));
        Assert.Equal("Password is too common. Choose a stronger password.", await Refusal(Secret, token, "password1"));
        Assert.Equal(PasswordPolicy.Validate("alice-alice-alice", "alice"), await Refusal(Secret, token, "alice-alice-alice"));
        Assert.NotNull(await _fixture.WithUnitOfWork(uow => _fixture.Auth.AuthenticateAsync(uow, "alice", Password)));
    }
}
