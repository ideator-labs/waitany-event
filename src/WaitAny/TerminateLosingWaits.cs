using WorkflowCore.Interface;
using WorkflowCore.Models;

namespace WaitAny;

/// <summary>
/// CancelCondition marks a losing WaitFor pointer Cancelled but leaves its event subscription
/// in the store until a matching event shows up. This step deletes those leftovers.
/// </summary>
public class TerminateLosingWaits(ISubscriptionRepository subscriptions) : StepBodyAsync
{
    public override async Task<ExecutionResult> RunAsync(IStepExecutionContext context)
    {
        var cancelledWaits = context.Workflow.ExecutionPointers
            .Where(p => p.Status == PointerStatus.Cancelled && p.EventName != null);

        foreach (var pointer in cancelledWaits)
        {
            var leftovers = await subscriptions.GetSubscriptions(pointer.EventName, pointer.EventKey, DateTime.MaxValue);
            foreach (var sub in leftovers.Where(s => s.WorkflowId == context.Workflow.Id && s.ExecutionPointerId == pointer.Id).ToList())
            {
                await subscriptions.TerminateSubscription(sub.Id);
            }
        }

        return ExecutionResult.Next();
    }
}
