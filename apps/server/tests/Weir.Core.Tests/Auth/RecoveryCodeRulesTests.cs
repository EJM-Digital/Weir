using Weir.Core.Auth;

namespace Weir.Core.Tests.Auth;

/// <summary>What a recovery code looks like, how a typed one is read, and that a set never repeats a lookup.</summary>
public sealed class RecoveryCodeRulesTests
{
    [Fact]
    public void A_code_is_four_groups_of_five_from_an_alphabet_without_look_alike_characters()
    {
        var code = RecoveryCodeRules.Generate();

        Assert.Matches("^[A-HJ-NP-Z2-9]{5}(-[A-HJ-NP-Z2-9]{5}){3}$", code);
    }

    [Fact]
    public void A_set_is_ten_different_codes_each_with_its_own_lookup()
    {
        var set = RecoveryCodeRules.NewSet();

        Assert.Equal(RecoveryCodeRules.SetSize, set.Count);
        Assert.Equal(set.Count, set.Distinct().Count());
        Assert.Equal(set.Count, set.Select(code => code[..5]).Distinct().Count());
    }

    [Fact]
    public void A_code_that_repeats_a_lookup_already_in_the_set_is_replaced()
    {
        var offered = new Queue<string>(
        [
            "AAAAA-BBBBB-CCCCC-DDDDD", "AAAAA-EEEEE-FFFFF-GGGGG", "HHHHH-BBBBB-CCCCC-DDDDD", "JJJJJ-BBBBB-CCCCC-DDDDD",
            "KKKKK-BBBBB-CCCCC-DDDDD", "LLLLL-BBBBB-CCCCC-DDDDD", "MMMMM-BBBBB-CCCCC-DDDDD", "NNNNN-BBBBB-CCCCC-DDDDD",
            "PPPPP-BBBBB-CCCCC-DDDDD", "QQQQQ-BBBBB-CCCCC-DDDDD", "RRRRR-BBBBB-CCCCC-DDDDD",
        ]);

        var set = RecoveryCodeRules.NewSet(offered.Dequeue);

        Assert.Equal(RecoveryCodeRules.SetSize, set.Count);
        Assert.DoesNotContain("AAAAA-EEEEE-FFFFF-GGGGG", set);
        Assert.Equal("RRRRR-BBBBB-CCCCC-DDDDD", set[^1]);
    }

    [Theory]
    [InlineData("ABCDE-FGHJK-LMNPQ-RSTUV")]
    [InlineData("abcde-fghjk-lmnpq-rstuv")]
    [InlineData("  abcde fghjk lmnpq rstuv  ")]
    [InlineData("ABCDEFGHJKLMNPQRSTUV")]
    public void A_typed_code_is_read_whatever_its_case_spaces_or_dashes(string typed)
    {
        Assert.True(RecoveryCodeRules.TryParse(typed, out var code, out var lookup));

        Assert.Equal("ABCDE-FGHJK-LMNPQ-RSTUV", code);
        Assert.Equal("ABCDE", lookup);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ABCDE-FGHJK-LMNPQ")]
    [InlineData("ABCDE-FGHJK-LMNPQ-RSTUVW")]
    [InlineData("ABCDE-FGHJK-LMNPQ-RSTU0")]
    [InlineData("ABCDE-FGHJK-LMNPQ-RSTU!")]
    public void Something_that_is_not_a_code_is_not_read(string? typed)
    {
        Assert.False(RecoveryCodeRules.TryParse(typed, out var code, out var lookup));

        Assert.Equal(string.Empty, code);
        Assert.Equal(string.Empty, lookup);
    }
}
