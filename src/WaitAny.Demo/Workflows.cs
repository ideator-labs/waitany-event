using WaitAny;
using WorkflowCore.Interface;
using WorkflowCore.Models;

namespace WaitAny.Demo;

public class ApprovalData : IWaitAnyData
{
    public string RequestId { get; set; } = "";
    public string? Winner { get; set; }
    public object? WinnerPayload { get; set; }
    public int AfterWaitRuns { get; set; }
}

public class ApprovalWorkflow : IWorkflow<ApprovalData>
{
    public string Id => "Approval";
    public int Version => 1;

    public void Build(IWorkflowBuilder<ApprovalData> builder) => builder
        .StartWith(_ => ExecutionResult.Next())
        .WaitAny(data => data.RequestId, "Approve", "Reject")
        .Then(context =>
        {
            ((ApprovalData)context.Workflow.Data).AfterWaitRuns++;
            return ExecutionResult.Next();
        });
}

/// <summary>The pattern from issue #428: Parallel + WaitFor with no CancelCondition.</summary>
public class BaselineWorkflow : IWorkflow<ApprovalData>
{
    public string Id => "Baseline";
    public int Version => 1;

    public void Build(IWorkflowBuilder<ApprovalData> builder) => builder
        .StartWith(_ => ExecutionResult.Next())
        .Parallel()
            .Do(branch => branch
                .WaitFor("Approve", data => data.RequestId)
                .Output(data => data.Winner, _ => "Approve"))
            .Do(branch => branch
                .WaitFor("Reject", data => data.RequestId)
                .Output(data => data.Winner, _ => "Reject"))
        .Join()
        .Then(context =>
        {
            ((ApprovalData)context.Workflow.Data).AfterWaitRuns++;
            return ExecutionResult.Next();
        });
}
