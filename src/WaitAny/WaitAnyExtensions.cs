using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using WorkflowCore.Interface;

namespace WaitAny;

public static class WaitAnyExtensions
{
    public static IServiceCollection AddWaitAny(this IServiceCollection services) =>
        services.AddTransient<TerminateLosingWaits>();

    /// <summary>
    /// Waits until the first of <paramref name="eventNames"/> is published for <paramref name="eventKey"/>.
    /// The winner's name and EventData land on <see cref="IWaitAnyData"/>; the other waits are cancelled
    /// and their subscriptions removed.
    /// </summary>
    public static IStepBuilder<TData, TerminateLosingWaits> WaitAny<TData, TStep>(
        this IStepBuilder<TData, TStep> builder,
        Expression<Func<TData, string>> eventKey,
        params string[] eventNames)
        where TData : IWaitAnyData
        where TStep : IStepBody
    {
        if (eventNames.Length < 2)
            throw new ArgumentException("WaitAny needs at least two event names.", nameof(eventNames));
        if (eventNames.Distinct().Count() != eventNames.Length)
            throw new ArgumentException("WaitAny event names must be unique.", nameof(eventNames));

        var parallel = builder.Parallel();
        foreach (var name in eventNames)
        {
            parallel = parallel.Do(branch => branch
                .WaitFor(name, eventKey, cancelCondition: data => data.Winner != null)
                .Output((step, data) =>
                {
                    data.Winner ??= name;
                    if (data.Winner == name)
                        data.WinnerPayload = step.EventData;
                }));
        }

        return parallel.Join().Then<TerminateLosingWaits>();
    }
}
