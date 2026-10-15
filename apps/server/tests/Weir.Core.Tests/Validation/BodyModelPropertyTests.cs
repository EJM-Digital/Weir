using System.Globalization;
using FsCheck;
using FsCheck.Fluent;
using Weir.Core.Json;
using Weir.Core.Validation;
using Weir.TestSupport;

namespace Weir.Core.Tests.Validation;

/// <summary>
/// Request bodies read through <see cref="BodyModel"/> and <see cref="FieldRules"/>: whatever a client sends in a field, the field is
/// either read as the type it was asked for or reported with an error, never both and never an exception.
/// </summary>
public sealed class BodyModelPropertyTests
{
    private const long Default = 7;

    private static readonly Gen<WireValue> AnyField = WireValueGenerators.Value(2);

    private static readonly Gen<WireValue> NumberField = Gen.Frequency(
        (3, AnyField),
        (3, WireValueGenerators.AnyInteger.Select(value => (WireValue)new WireInteger(value))),
        (2, Gen.Choose(-5, 120).Select(value => (WireValue)new WireInteger(value))),
        (2, Gen.Choose(-5, 120).Select(value => (WireValue)new WireString(value.ToString(CultureInfo.InvariantCulture)))),
        (1, Gen.Choose(-5, 120).Select(value => (WireValue)new WireNumber(value))));

    [Fact]
    public void A_number_is_in_its_range_when_the_body_is_valid_and_the_default_with_an_error_when_it_is_not()
    {
        PropertyChecks.ForAll(NumberField, input =>
        {
            var (model, issues) = Model("n", input);
            var read = model.Number("n", Default, required: false, ge: 1, le: 100);
            model.Finish(ExtraFields.Forbid);

            Assert.Equal(issues.Any, !model.IsValid);
            if (model.IsValid)
            {
                Assert.InRange(read, 1, 100);
            }
            else
            {
                Assert.Equal(Default, read);
            }
        });
    }

    [Fact]
    public void A_whole_number_in_range_is_read_exactly_as_a_number_a_text_or_a_float()
    {
        var whole = Gen.Choose(1, 100);
        var spelled = from number in whole
                      from form in Gen.Elements(Spelling.Integer, Spelling.Text, Spelling.PaddedText, Spelling.Float, Spelling.FloatText)
                      select (number, input: Spell(number, form));

        PropertyChecks.ForAll(spelled, found =>
        {
            var (model, issues) = Model("n", found.input);
            Assert.Equal(found.number, model.Number("n", Default, required: false, ge: 1, le: 100));
            Assert.False(issues.Any);
        });
    }

    [Fact]
    public void A_text_is_read_whole_when_it_is_text_in_length_and_refused_when_it_is_anything_else()
    {
        PropertyChecks.ForAll(Gen.Frequency((1, AnyField), (3, WireValueGenerators.Text.Select(text => (WireValue)new WireString(text)))), input =>
        {
            var (model, issues) = Model("s", input);
            var read = model.Str("s", minLength: 1, maxLength: 5);
            var points = input is WireString text ? text.Value.EnumerateRunes().Count() : -1;
            var expectedValid = input is WireString && points is >= 1 and <= 5;

            Assert.Equal(expectedValid, model.IsValid);
            Assert.Equal(!expectedValid, issues.Any);
            Assert.Equal(expectedValid ? ((WireString)input).Value : string.Empty, read);
        });
    }

    [Fact]
    public void A_switch_is_read_from_its_spellings_and_refused_with_an_error_otherwise()
    {
        PropertyChecks.ForAll(AnyField, input =>
        {
            var (model, issues) = Model("b", input);
            var read = model.Bool("b", defaultValue: true);
            Assert.Equal(issues.Any, !model.IsValid);
            if (!model.IsValid)
            {
                Assert.True(read);
            }
        });
    }

    [Fact]
    public void Whatever_each_field_holds_reading_every_kind_of_field_neither_throws_nor_keeps_a_read_that_was_refused()
    {
        string[] literal = ["a", "b"];
        Action<BodyModel>[] readers =
        [
            model => model.OptionalStr("f", maxLength: 3),
            model => model.OptionalInt("f", ge: 0, le: 9),
            model => model.OptionalBool("f"),
            model => model.Literal("f", literal, "a"),
            model => model.StrList("f", []),
            model => model.IntList("f"),
            model => model.Dict("f"),
            model => model.OptionalDict("f"),
            model => model.OptionalDateTime("f"),
            model => model.Number("f", 0, required: true),
        ];

        PropertyChecks.ForAll(AnyField.Zip(Gen.Elements(readers)), found =>
        {
            var (input, read) = found;
            var (model, issues) = Model("f", input);
            read(model);
            model.Finish(ExtraFields.Forbid);

            Assert.Equal(issues.Any, !model.IsValid);
            Assert.All(issues.All, issue => Assert.False(string.IsNullOrEmpty(issue.Type)));
            _ = new RequestValidationException(issues.All).ToBody();
        });
    }

    [Fact]
    public void Keys_nobody_declared_are_reported_when_extras_are_forbidden_and_ignored_otherwise()
    {
        var keys = Gen.Choose(0, 5).SelectMany(count => Gen.ArrayOf(Gen.Elements("a", "b", "c", "d", "e"), count));
        PropertyChecks.ForAll(keys.Zip(Gen.Elements(ExtraFields.Forbid, ExtraFields.Ignore)), found =>
        {
            var (sent, extras) = found;
            var body = new WireObject();
            foreach (var key in sent)
            {
                body.Set(key, 1);
            }

            var issues = new ValidationIssues();
            var model = new BodyModel(body, issues);
            model.OptionalInt("a");
            model.AcceptAndIgnore(["b"]);
            model.Finish(extras);

            var undeclared = body.Keys.Where(key => key is not ("a" or "b")).ToList();
            var reported = issues.All.Where(issue => issue.Type == "extra_forbidden").Select(issue => (string)issue.Loc[1]).ToList();
            Assert.Equal(extras == ExtraFields.Forbid ? undeclared : [], reported);
            Assert.Equal(extras != ExtraFields.Forbid || undeclared.Count == 0, model.IsValid);
        });
    }

    [Fact]
    public void A_uuid_is_accepted_in_every_spelling_of_one_and_only_for_text_that_is_one()
    {
        var uuid = from guid in Gen.Fresh(Guid.NewGuid)
                   from format in Gen.Elements("D", "N", "B")
                   from upper in Gen.Elements(true, false)
                   select upper ? guid.ToString(format).ToUpperInvariant() : guid.ToString(format);
        var soup = Gen.Choose(0, 40).SelectMany(length => Gen.ArrayOf(Gen.Elements("0123456789abcdefABCDEF-{}x:".ToCharArray()), length)).Select(chars => new string(chars));

        PropertyChecks.ForAll(Gen.OneOf(uuid, soup), text =>
        {
            var issues = new ValidationIssues();
            var accepted = FieldRules.TryUuid(text, ["path", "id"], issues, out var value);
            Assert.Equal(!accepted, issues.Any);
            if (accepted)
            {
                var bare = text.Replace("{", string.Empty, StringComparison.Ordinal).Replace("}", string.Empty, StringComparison.Ordinal).Replace("urn:uuid:", string.Empty, StringComparison.Ordinal);
                Assert.True(Guid.TryParse(bare, out var expected), text);
                Assert.Equal(expected, value);
            }
        });
    }

    private static (BodyModel Model, ValidationIssues Issues) Model(string field, WireValue input)
    {
        var issues = new ValidationIssues();
        return (new BodyModel(new WireObject().Set(field, input), issues), issues);
    }

    private static WireValue Spell(int number, Spelling form) => form switch
    {
        Spelling.Integer => new WireInteger(number),
        Spelling.Text => new WireString(number.ToString(CultureInfo.InvariantCulture)),
        Spelling.PaddedText => new WireString(" " + number.ToString(CultureInfo.InvariantCulture) + "\t"),
        Spelling.Float => new WireNumber(number),
        _ => new WireString(number.ToString(CultureInfo.InvariantCulture) + ".0"),
    };

    private enum Spelling
    {
        Integer,
        Text,
        PaddedText,
        Float,
        FloatText,
    }
}
