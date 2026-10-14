using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WaitAny;
using WaitAny.Demo;
using WorkflowCore.Interface;
using WorkflowCore.Models;

var services = new ServiceCollection()
    .AddLogging(l => l.SetMinimumLevel(LogLevel.Warning).AddConsole(c => c.LogToStandardErrorThreshold = LogLevel.Trace))
    .AddWorkflow()
    .AddWaitAny()
    .BuildServiceProvider();

var host = services.GetRequiredService<IWorkflowHost>();
var store = services.GetRequiredService<IPersistenceProvider>();
host.RegisterWorkflow<ApprovalWorkflow, ApprovalData>();
host.RegisterWorkflow<BaselineWorkflow, ApprovalData>();
await host.StartAsync(CancellationToken.None);

string[] events = ["Approve", "Reject"];
var settle = TimeSpan.FromSeconds(5);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

async Task<string> Start(string workflow, string key)
{
    var id = await host.StartWorkflow(workflow, new ApprovalData { RequestId = key });
    await WaitUntil(id, i => i.ExecutionPointers.Count(p => p.Status == PointerStatus.WaitingForEvent) == events.Length, settle);
    return id;
}

async Task<WorkflowInstance> WaitUntil(string id, Func<WorkflowInstance, bool> done, TimeSpan timeout)
{
    var clock = Stopwatch.StartNew();
    while (true)
    {
        var instance = await store.GetWorkflowInstance(id);
        if (done(instance) || clock.Elapsed > timeout) return instance;
        await Task.Delay(50);
    }
}

async Task<object> Snapshot(string scenario, string id, string key, string[] published, Stopwatch clock)
{
    var instance = await store.GetWorkflowInstance(id);
    var data = (ApprovalData)instance.Data;
    var open = 0;
    foreach (var name in events)
        open += (await store.GetSubscriptions(name, key, DateTime.MaxValue)).Count(s => s.WorkflowId == id);
    return new
    {
        scenario,
        workflow = instance.WorkflowDefinitionId,
        key,
        published,
        status = instance.Status.ToString(),
        winner = data.Winner,
        winnerPayload = data.WinnerPayload?.ToString(),
        afterWaitRuns = data.AfterWaitRuns,
        waits = instance.ExecutionPointers
            .Where(p => p.EventName != null)
            .Select(p => new { @event = p.EventName, pointer = p.Status.ToString() })
            .OrderBy(w => w.@event),
        openSubscriptions = open,
        elapsedMs = clock.ElapsedMilliseconds,
    };
}

async Task<object> PublishOnly(string scenario, string workflow, string key, params (string Name, string Payload)[] toPublish)
{
    var id = await Start(workflow, key);
    var clock = Stopwatch.StartNew();
    foreach (var (name, payload) in toPublish)
        await host.PublishEvent(name, key, payload);
    await WaitUntil(id, i => i.Status == WorkflowStatus.Complete, settle);
    return await Snapshot(scenario, id, key, toPublish.Select(e => e.Name).ToArray(), clock);
}

async Task<object> LateLoser()
{
    const string key = "req-late";
    var id = await Start("Approval", key);
    var clock = Stopwatch.StartNew();
    await host.PublishEvent("Approve", key, "yes");
    await WaitUntil(id, i => i.Status == WorkflowStatus.Complete, settle);
    await host.PublishEvent("Reject", key, "too late");
    await Task.Delay(1000);
    return await Snapshot("late_loser", id, key, ["Approve", "Reject"], clock);
}

async Task<object> OtherKeyUntouched()
{
    var target = await Start("Approval", "req-a");
    var bystander = await Start("Approval", "req-b");
    var clock = Stopwatch.StartNew();
    await host.PublishEvent("Approve", "req-a", "yes");
    await WaitUntil(target, i => i.Status == WorkflowStatus.Complete, settle);
    await Task.Delay(500);
    return await Snapshot("other_key_untouched", bystander, "req-b", [], clock);
}

var results = new List<object>
{
    await PublishOnly("approve_only", "Approval", "req-1", ("Approve", "yes")),
    await PublishOnly("reject_only", "Approval", "req-2", ("Reject", "no")),
    await PublishOnly("both_back_to_back", "Approval", "req-3", ("Approve", "yes"), ("Reject", "no")),
    await LateLoser(),
    await OtherKeyUntouched(),
    await PublishOnly("baseline_no_cancel", "Baseline", "req-base", ("Approve", "yes")),
};

foreach (var result in results)
    Console.WriteLine(JsonSerializer.Serialize(result, json));

await host.StopAsync(CancellationToken.None);
