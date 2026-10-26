using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameTuner.Core.Infrastructure;
using GameTuner.Core.Models;

namespace GameTuner.Core.Optimization;

public sealed record AiAdvice(
    string CandidateId,
    string Explanation,
    bool UsedFallback);

public interface IAiAdvisor
{
    Task<AiAdvice> ReviewAsync(
        TuningGoal goal,
        PerformanceMetrics metrics,
        IReadOnlyList<TuningCandidate> allowedCandidates,
        CancellationToken cancellationToken = default);
}

public sealed class RuleBasedAiAdvisor : IAiAdvisor
{
    public Task<AiAdvice> ReviewAsync(
        TuningGoal goal,
        PerformanceMetrics metrics,
        IReadOnlyList<TuningCandidate> allowedCandidates,
        CancellationToken cancellationToken = default)
    {
        var selected = allowedCandidates.FirstOrDefault();
        var explanation =
            $"平均 {metrics.AverageFps:0.0} FPS，估算 1% Low {metrics.LowFps:0.0} FPS；" +
            $"目标 {goal.TargetFps:0} FPS。瓶颈判断：{DescribeBottleneck(metrics.Bottleneck)}。";
        return Task.FromResult(
            new AiAdvice(selected?.Id ?? "", explanation, UsedFallback: true));
    }

    public static string DescribeBottleneck(BottleneckKind bottleneck) =>
        bottleneck switch
        {
            BottleneckKind.Gpu => "GPU 满载，降低高开销画质有效",
            BottleneckKind.Cpu => "CPU 受限，继续降画质收益有限",
            BottleneckKind.Vram => "显存接近上限，应优先降低纹理与阴影",
            BottleneckKind.None => "当前没有明显单项瓶颈",
            _ => "数据不足，暂时无法判断"
        };
}

public sealed class OpenAiCompatibleAdvisor(
    HttpClient httpClient,
    AppSettings settings,
    ICredentialStore credentialStore,
    IAiAdvisor fallback) : IAiAdvisor
{
    public async Task<AiAdvice> ReviewAsync(
        TuningGoal goal,
        PerformanceMetrics metrics,
        IReadOnlyList<TuningCandidate> allowedCandidates,
        CancellationToken cancellationToken = default)
    {
        if (allowedCandidates.Count == 0 ||
            string.IsNullOrWhiteSpace(settings.AiModel) ||
            string.IsNullOrWhiteSpace(settings.AiEndpoint))
        {
            return await fallback.ReviewAsync(
                goal,
                metrics,
                allowedCandidates,
                cancellationToken);
        }

        var apiKey = credentialStore.Read(settings.AiApiKeyCredentialName);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return await fallback.ReviewAsync(
                goal,
                metrics,
                allowedCandidates,
                cancellationToken);
        }

        try
        {
            var endpoint = settings.AiEndpoint.TrimEnd('/') + "/chat/completions";
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = JsonContent.Create(new
            {
                model = settings.AiModel,
                temperature = 0.1,
                response_format = new { type = "json_object" },
                messages = new object[]
                {
                    new
                    {
                        role = "system",
                        content =
                            "你是PC游戏性能调优顾问。只能从给定候选中选择，输出JSON，" +
                            "字段 candidateId 和 explanation。解释使用简体中文，不得声称精确测量。"
                    },
                    new
                    {
                        role = "user",
                        content = JsonSerializer.Serialize(
                            new
                            {
                                goal,
                                metrics,
                                candidates = allowedCandidates.Select(candidate => new
                                {
                                    candidate.Id,
                                    candidate.Label,
                                    candidate.Preset,
                                    candidate.SettingOverrides,
                                    candidate.Rationale
                                })
                            },
                            JsonFileStore.Options)
                    }
                }
            });

            using var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<JsonObject>(
                cancellationToken: cancellationToken);
            var content = payload?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new InvalidDataException("模型没有返回内容。");
            }

            var parsed = JsonNode.Parse(content) as JsonObject;
            var candidateId = parsed?["candidateId"]?.GetValue<string>() ?? "";
            var explanation = parsed?["explanation"]?.GetValue<string>() ?? "";
            if (!allowedCandidates.Any(candidate => candidate.Id == candidateId) ||
                string.IsNullOrWhiteSpace(explanation))
            {
                throw new InvalidDataException("模型输出未通过候选白名单校验。");
            }

            return new AiAdvice(candidateId, explanation, UsedFallback: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return await fallback.ReviewAsync(
                goal,
                metrics,
                allowedCandidates,
                cancellationToken);
        }
    }
}
