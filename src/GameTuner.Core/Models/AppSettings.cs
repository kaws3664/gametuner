namespace GameTuner.Core.Models;

public sealed class AppSettings
{
    public string AiEndpoint { get; set; } = "https://api.openai.com/v1";
    public string AiModel { get; set; } = "";
    public string AiWireApi { get; set; } = "chat";
    public string AiUserAgent { get; set; } = "";
    public string AiApiKeyCredentialName { get; set; } = "GameTuner.OpenAI.ApiKey";
    public int ExitWaitTimeoutSeconds { get; set; } = 900;
    public int CaptureDurationSeconds { get; set; } = 300;

    /// <summary>Benchmark 链路每轮结束后的自动退出宽限秒数：&gt;0 时超时自动关闭游戏进入下一轮；0 表示仍等待用户手动退出。</summary>
    public int BenchmarkExitGraceSeconds { get; set; } = 15;

    /// <summary>等待 Benchmark 结果文件时，每隔多少秒向用户发出一次系统提醒（提示进入游戏内运行基准测试）。</summary>
    public int BenchmarkReminderSeconds { get; set; } = 180;
    public string TelemetryLogDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "nvlog");
}
