namespace WaitAny;

public interface IWaitAnyData
{
    string? Winner { get; set; }
    object? WinnerPayload { get; set; }
}
