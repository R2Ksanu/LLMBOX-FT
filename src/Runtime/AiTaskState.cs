namespace LlmodelBox.Runtime
{
    // The lifecycle of an agent task. Later phases (planning, execution, verification, self-repair)
    // drive a RuntimeTask through these states; Phase 1 only records them.
    public enum AiTaskState
    {
        Created,
        Planning,
        Executing,
        WaitingForApproval,
        Verifying,
        Repairing,
        Completed,
        Failed,
        Cancelled
    }
}
