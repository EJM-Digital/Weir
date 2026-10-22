using System.Runtime.CompilerServices;
using Weir.Infrastructure.Scheduling;

namespace Weir.Infrastructure.Tests.Scheduling;

/// <summary>
/// What every periodic task tells the scheduler about itself: a name the System screen and the logs can use, how often it runs, and
/// what is said when a run fails. The words are constants of the task, so they are read without the services the task runs on.
/// </summary>
public sealed class PeriodicTaskDescriptionTests
{
    /// <summary>Tasks whose words come from the task they wrap or from their settings, so they cannot be read bare; their tests build them whole.</summary>
    private static readonly HashSet<string> ReadFromSettings = ["SpedUpPeriodicTask", "JobRowsRetentionTask"];

    public static TheoryData<Type> TaskTypes()
    {
        var data = new TheoryData<Type>();
        foreach (var type in typeof(IPeriodicTask).Assembly.GetTypes()
                     .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IPeriodicTask).IsAssignableFrom(type) && !ReadFromSettings.Contains(type.Name))
                     .OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            data.Add(type);
        }

        return data;
    }

    [Fact]
    public void There_are_periodic_tasks_to_describe()
    {
        Assert.True(TaskTypes().Count >= 15);
    }

    [Theory]
    [MemberData(nameof(TaskTypes))]
    public void A_periodic_task_names_itself_and_says_what_a_failed_run_means(Type type)
    {
        var task = (IPeriodicTask)RuntimeHelpers.GetUninitializedObject(type);

        Assert.Matches("^[a-z]+(-[a-z]+)*$", task.Name);
        Assert.True(task.Interval > TimeSpan.Zero);
        Assert.False(string.IsNullOrWhiteSpace(task.FailureMessage));
        Assert.True(task.FailureCooldown is null || task.FailureCooldown > TimeSpan.Zero);
        Assert.True(task.Label is null || task.Label.Length > 0);
        _ = task.RunAtStart;
    }
}
