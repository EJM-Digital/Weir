using System.Reflection;

namespace Weir.Infrastructure.Tests;

/// <summary>Proves a constructor names, and refuses, every part it cannot work without.</summary>
internal static class ConstructorGuards
{
    /// <summary>
    /// Builds the type's only constructor once for each required parameter with that argument missing, and expects the
    /// <see cref="ArgumentNullException"/> to name it. A parameter with a default value may be left out and is not tried.
    /// </summary>
    public static void AssertEachRequiredPartIsRefused<T>(Func<object?[]> arguments)
    {
        var constructor = typeof(T).GetConstructors().Single();
        var parameters = constructor.GetParameters();
        for (var index = 0; index < parameters.Length; index++)
        {
            if (parameters[index].HasDefaultValue)
            {
                continue;
            }

            var withMissingPart = arguments();
            withMissingPart[index] = null;

            var failure = Assert.Throws<TargetInvocationException>(() => constructor.Invoke(withMissingPart));

            var refusal = Assert.IsType<ArgumentNullException>(failure.InnerException);
            Assert.Equal(parameters[index].Name, refusal.ParamName);
        }
    }
}
