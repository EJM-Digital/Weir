using System.Text.Json.Nodes;
using FsCheck;
using FsCheck.Fluent;

namespace Weir.Contract.Tests.MediaManagers.Recorded;

/// <summary>
/// Copies of a recorded body with a few things changed in it: a member dropped, set to another kind of value or to an odd one, or an
/// unknown member added. The changes are generated from a seed, so a failing run names the seed and the body it sent.
/// </summary>
internal static class BodyMutations
{
    private static readonly string[] PathLike =
    [
        @"..\..\Windows\System32", "../../etc/passwd", @"C:\Downloads\Completed\Movies\..\..\x.mkv", @"\\server\share\x.mkv", "/", ".", "C:", "C:\\",
        "a\0b", @"C:\Downloads\Completed\Movies\", @"c:\DOWNLOADS\completed\movies\Film.MKV", "C:/Downloads/Completed/Movies/Film.mkv", "\u202e.vkm",
    ];

    /// <summary>The body changed by one to three mutations.</summary>
    public static Gen<JsonObject> Of(JsonObject recorded) =>
        from count in Gen.Choose(1, 3)
        from mutations in Gen.ArrayOf(Mutation(recorded), count)
        select mutations.Aggregate((JsonObject)recorded.DeepClone(), (body, mutate) => mutate(body));

    /// <summary>The body's text with one character dropped, replaced, added, or the text cut short.</summary>
    public static Gen<string> AsBrokenText(JsonObject recorded)
    {
        var text = recorded.ToJsonString();
        return from position in Gen.Choose(0, text.Length - 1)
               from edit in Gen.Elements(Edit.Drop, Edit.Replace, Edit.Add, Edit.CutShort)
               from character in Gen.Elements("{", "}", "[", "]", ",", ":", "\"", "\\", "\0", "-", "é")
               select edit switch
               {
                   Edit.Drop => text.Remove(position, 1),
                   Edit.Replace => text.Remove(position, 1).Insert(position, character),
                   Edit.Add => text.Insert(position, character),
                   _ => text[..position],
               };
    }

    private static Gen<Func<JsonObject, JsonObject>> Mutation(JsonObject recorded)
    {
        var members = recorded.Select(member => member.Key).ToArray();
        return from operation in Gen.Elements(Operation.Drop, Operation.Replace, Operation.Replace, Operation.Add)
               from member in Gen.Elements(members)
               from value in Value
               from unknown in Gen.Elements("extra", "sourceFiles", "queueItemId", "__proto__", "", "a b")
               select (Func<JsonObject, JsonObject>)(body =>
               {
                   switch (operation)
                   {
                       case Operation.Drop:
                           body.Remove(member);
                           break;
                       case Operation.Replace:
                           body[member] = value();
                           break;
                       default:
                           body[unknown] = value();
                           break;
                   }

                   return body;
               });
    }

    /// <summary>A value of another kind or an odd one, made fresh each time because a node belongs to one parent.</summary>
    private static Gen<Func<JsonNode?>> Value { get; } = Gen.OneOf(
        Gen.Elements<Func<JsonNode?>>(
            () => null,
            () => true,
            () => 0,
            () => -1,
            () => 1.5,
            () => JsonNode.Parse("9223372036854775808"),
            () => JsonNode.Parse("1e999"),
            () => JsonNode.Parse("-0"),
            () => string.Empty,
            () => "   ",
            () => new string('a', 5000),
            () => "😀 é \u202e",
            () => new JsonObject(),
            () => new JsonArray(),
            () => Nested(40),
            () => new JsonArray(1, 2, 3),
            () => new JsonArray("a.mkv", 1, null, @"..\x.mkv", string.Empty)),
        Gen.Elements(PathLike).Select(path => (Func<JsonNode?>)(() => path)),
        Gen.Elements(PathLike).Select(path => (Func<JsonNode?>)(() => new JsonArray(path, path + "2"))));

    private static JsonNode Nested(int depth)
    {
        JsonNode inner = new JsonArray();
        for (var i = 0; i < depth; i++)
        {
            inner = new JsonArray(inner);
        }

        return inner;
    }

    private enum Operation
    {
        Drop,
        Replace,
        Add,
    }

    private enum Edit
    {
        Drop,
        Replace,
        Add,
        CutShort,
    }
}
