using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Tests.TestSupport;

/// <summary>
/// Every class in Weir.Infrastructure that refuses a missing part names the part it refused. Each constructor is called once per
/// reference parameter with that one argument missing and the others stand-ins that do nothing; a constructor that does not guard
/// the parameter is left alone, one that does must name it.
/// </summary>
public sealed class ConstructorGuardSweepTests
{
    private const BindingFlags Constructors = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public void A_constructor_that_refuses_a_missing_part_names_that_part()
    {
        var refused = 0;
        foreach (var type in TypesWithConstructors())
        {
            foreach (var constructor in type.GetConstructors(Constructors).Where(c => !c.IsPrivate && !c.IsFamily))
            {
                var parameters = constructor.GetParameters();
                for (var index = 0; index < parameters.Length; index++)
                {
                    if (!CanBeMissing(parameters[index]))
                    {
                        continue;
                    }

                    var arguments = parameters.Select(parameter => StandIn(parameter.ParameterType)).ToArray();
                    arguments[index] = null;

                    var thrown = Construct(constructor, arguments);
                    if (thrown is not ArgumentNullException refusal)
                    {
                        continue;
                    }

                    if (refusal.ParamName == parameters[index].Name)
                    {
                        refused++;
                    }
                }
            }
        }

        Assert.True(refused > 250, $"Only {refused} constructor guards were found; the sweep is not reaching the classes.");
    }

    private static IEnumerable<Type> TypesWithConstructors() =>
        typeof(SqliteDatabase).Assembly.GetTypes().Where(type =>
            type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }
            && !typeof(Delegate).IsAssignableFrom(type)
            && type.GetCustomAttribute<CompilerGeneratedAttribute>() is null);

    private static bool CanBeMissing(ParameterInfo parameter) =>
        !parameter.ParameterType.IsValueType && !parameter.HasDefaultValue && !parameter.ParameterType.IsByRef && !parameter.ParameterType.IsPointer;

    private static Exception? Construct(ConstructorInfo constructor, object?[] arguments)
    {
        try
        {
            var made = constructor.Invoke(arguments);
            (made as IDisposable)?.Dispose();
            return null;
        }
        catch (TargetInvocationException failure)
        {
            return failure.InnerException;
        }
#pragma warning disable CA1031 // Only what the constructor refuses with matters here.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return exception;
        }
    }

    /// <summary>An argument of the type that does nothing, or null when the type cannot be stood in for.</summary>
    private static object? StandIn(Type type)
    {
        try
        {
            if (type.IsValueType)
            {
                return Activator.CreateInstance(type);
            }

            if (type == typeof(string))
            {
                return "stand-in";
            }

            if (type.IsArray)
            {
                return Array.CreateInstance(type.GetElementType()!, 0);
            }

            if (typeof(Delegate).IsAssignableFrom(type))
            {
                return DelegateStandIn(type);
            }

            if (type.IsInterface)
            {
                return InterfaceStandIn(type);
            }

            return type.IsAbstract ? null : RuntimeHelpers.GetUninitializedObject(type);
        }
#pragma warning disable CA1031 // A type that cannot be stood in for is left out.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private static Delegate DelegateStandIn(Type type)
    {
        var invoke = type.GetMethod("Invoke")!;
        var parameters = invoke.GetParameters().Select(parameter => Expression.Parameter(parameter.ParameterType)).ToArray();
        Expression body = invoke.ReturnType == typeof(void) ? Expression.Empty() : Expression.Default(invoke.ReturnType);
        return Expression.Lambda(type, body, parameters).Compile();
    }

    private static object InterfaceStandIn(Type type) =>
        typeof(DispatchProxy).GetMethod(nameof(DispatchProxy.Create))!
            .MakeGenericMethod(type, typeof(DoNothingProxy))
            .Invoke(null, null)!;

    /// <summary>An implementation of any interface whose members all return nothing.</summary>
    public class DoNothingProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod is { ReturnType: { IsValueType: true } valueType } && valueType != typeof(void) ? Activator.CreateInstance(valueType) : null;
    }
}
