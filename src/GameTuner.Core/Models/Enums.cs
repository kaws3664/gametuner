namespace GameTuner.Core.Models;

public enum TuningMode
{
    Quality,
    Balanced,
    FrameRate
}

public enum QualityPreset
{
    Revert,
    Performance,
    Balanced,
    Recommended,
    Quality
}

public enum BottleneckKind
{
    Unknown,
    None,
    Gpu,
    Cpu,
    Vram
}

public enum SessionState
{
    Idle,
    Preparing,
    WaitingForGame,
    WaitingForBenchmark,
    WaitingForCapture,
    Capturing,
    WaitingForExit,
    AwaitingDecision,
    Applying,
    Completed,
    Failed,
    Cancelled
}

public enum TelemetryPrecision
{
    FrameAccurate,
    NvidiaAggregate,
    Estimated,
    UtilizationOnly
}

public enum RoundOutcome
{
    NotMeasured,
    MeetsTarget,
    MissesTarget,
    Invalid
}
