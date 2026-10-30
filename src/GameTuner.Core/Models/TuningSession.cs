namespace GameTuner.Core.Models;

public sealed class TuningSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public TuningGoal Goal { get; set; } = new();
    public SessionState State { get; set; } = SessionState.Idle;
    public List<RoundResult> Rounds { get; set; } = [];
    public TuningCandidate? FinalCandidate { get; set; }
    public TuningCandidate? PendingCandidate { get; set; }
    public string OriginalConfigPath { get; set; } = "";
    public string OriginalConfigHash { get; set; } = "";
    public string OriginalBackupPath { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? CompletedAt { get; set; }
}
