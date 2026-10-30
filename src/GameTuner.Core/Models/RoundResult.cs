namespace GameTuner.Core.Models;

public sealed class RoundResult
{
    public int RoundNumber { get; set; }
    public TuningCandidate Candidate { get; set; } = new();
    public PerformanceMetrics Metrics { get; set; } = new();
    public RoundOutcome Outcome { get; set; } = RoundOutcome.NotMeasured;
    public double Score { get; set; }
    public string Explanation { get; set; } = "";
    public string ConfigHash { get; set; } = "";
    public string ConfigSnapshotPath { get; set; } = "";
    public DateTimeOffset CompletedAt { get; set; } = DateTimeOffset.Now;
}
