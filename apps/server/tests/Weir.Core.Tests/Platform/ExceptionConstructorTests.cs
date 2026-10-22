using System.Reflection;

namespace Weir.Core.Tests.Platform;

/// <summary>Every exception type Weir.Core defines carries the message and the cause it was given through each standard constructor.</summary>
public sealed class ExceptionConstructorTests
{
    public static TheoryData<Type> ExceptionTypes()
    {
        var data = new TheoryData<Type>();
        foreach (var type in typeof(WeirVersion).Assembly.GetExportedTypes().Where(t => t.IsSubclassOf(typeof(Exception)) && !t.IsAbstract).OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            data.Add(type);
        }

        return data;
    }

    private static ConstructorInfo? Constructor(Type type, params Type[] parameters) => type.GetConstructor(parameters);

    [Theory]
    [MemberData(nameof(ExceptionTypes))]
    public void An_exception_with_no_arguments_can_be_created(Type type)
    {
        var constructor = Constructor(type);
        if (constructor is null)
        {
            return;
        }

        Assert.IsAssignableFrom<Exception>(constructor.Invoke([]));
    }

    [Theory]
    [MemberData(nameof(ExceptionTypes))]
    public void An_exception_keeps_the_message_it_was_given(Type type)
    {
        var constructor = Constructor(type, typeof(string));
        if (constructor is null)
        {
            return;
        }

        var exception = (Exception)constructor.Invoke(["the message"]);

        Assert.Contains("the message", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ExceptionTypes))]
    public void An_exception_keeps_the_message_and_the_cause_it_was_given(Type type)
    {
        var constructor = Constructor(type, typeof(string), typeof(Exception));
        if (constructor is null)
        {
            return;
        }

        var cause = new InvalidOperationException("underlying");

        var exception = (Exception)constructor.Invoke(["the message", cause]);

        Assert.Contains("the message", exception.Message, StringComparison.Ordinal);
        Assert.Same(cause, exception.InnerException);
    }
}
