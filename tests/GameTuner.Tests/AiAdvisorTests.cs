using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GameTuner.Core.Infrastructure;
using GameTuner.Core.Models;
using GameTuner.Core.Optimization;

namespace GameTuner.Tests;

public sealed class AiAdvisorTests
{
    private const string TestModel = "local-test-model";
    private const string TestApiKey = "local-test-key";
    private const string Explanation = "降低体积效果以缓解 GPU 瓶颈。";
    private const string AdviceJson = """{"candidateId":"balanced","explanation":"降低体积效果以缓解 GPU 瓶颈。"}""";
    private const string ChatResponse = """
        {"choices":[{"message":{"content":"{\"candidateId\":\"balanced\",\"explanation\":\"降低体积效果以缓解 GPU 瓶颈。\"}"}}]}
        """;
    private const string ResponsesResponse = """
        {
          "output": [
            {"type":"reasoning","content":[{"type":"output_text","text":"忽略非 message 输出"}]},
            {
              "type":"message",
              "content":[
                {"type":"refusal","text":"忽略非 output_text 内容"},
                {"type":"output_text","text":"{\"candidateId\":\"balanced\","},
                {"type":"output_text","text":"\"explanation\":\"降低体积效果"}
              ]
            },
            {"type":"message","content":[{"type":"output_text","text":"以缓解 GPU 瓶颈。\"}"}]}
          ]
        }
        """;

    private readonly TuningGoal _goal = new() { TargetFps = 60, Mode = TuningMode.Balanced };
    private readonly PerformanceMetrics _metrics = new()
    {
        AverageFps = 55,
        LowFps = 40,
        Bottleneck = BottleneckKind.Gpu
    };
    private readonly IReadOnlyList<TuningCandidate> _candidates =
    [
        new() { Id = "quality", Label = "保留画质", Preset = QualityPreset.Quality },
        new()
        {
            Id = "balanced",
            Label = "降低体积效果",
            Preset = QualityPreset.Balanced,
            SettingOverrides = new Dictionary<string, string> { ["VolumetricQuality"] = "1" },
            Rationale = "缓解 GPU 瓶颈"
        }
    ];

    [Fact]
    public async Task Chat_posts_expected_request_and_parses_message_content()
    {
        await using var server = new FakeOpenAiServer(ChatResponse);
        using var client = CreateHttpClient();
        var settings = new AppSettings { AiEndpoint = server.Endpoint, AiModel = TestModel };
        var advisor = CreateAdvisor(client, settings);

        var advice = await advisor.ReviewAsync(_goal, _metrics, _candidates);

        Assert.Equal("chat", settings.AiWireApi);
        AssertAdvice(advice);
        var request = Assert.Single(server.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/v1/chat/completions", request.Path);
        Assert.Equal("Bearer " + TestApiKey, request.Headers["Authorization"]);
        Assert.Equal(TestModel, request.Body.GetProperty("model").GetString());
        Assert.Equal(0.1, request.Body.GetProperty("temperature").GetDouble());
        Assert.Equal("json_object", request.Body.GetProperty("response_format").GetProperty("type").GetString());
        var messages = request.Body.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Contains("candidateId", messages[0].GetProperty("content").GetString()!);
        Assert.Contains("explanation", messages[0].GetProperty("content").GetString()!);
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        AssertPrompt(messages[1].GetProperty("content").GetString()!);
    }

    [Fact]
    public async Task Responses_posts_expected_request_and_concatenates_only_message_output_text()
    {
        await using var server = new FakeOpenAiServer(ResponsesResponse);
        using var client = CreateHttpClient();
        var advisor = CreateAdvisor(client, CreateSettings(server, "responses"));

        var advice = await advisor.ReviewAsync(_goal, _metrics, _candidates);

        AssertAdvice(advice);
        var request = Assert.Single(server.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/v1/responses", request.Path);
        Assert.Equal("Bearer " + TestApiKey, request.Headers["Authorization"]);
        Assert.Equal(TestModel, request.Body.GetProperty("model").GetString());
        Assert.Equal(3000, request.Body.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal("low", request.Body.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.False(request.Body.TryGetProperty("temperature", out _));
        Assert.False(request.Body.TryGetProperty("response_format", out _));
        Assert.False(request.Body.TryGetProperty("messages", out _));
        var input = request.Body.GetProperty("input");
        Assert.Equal(1, input.GetArrayLength());
        Assert.Equal("user", input[0].GetProperty("role").GetString());
        var content = input[0].GetProperty("content");
        Assert.Equal(1, content.GetArrayLength());
        Assert.Equal("input_text", content[0].GetProperty("type").GetString());
        var prompt = content[0].GetProperty("text").GetString()!;
        Assert.Contains("candidateId", prompt);
        Assert.Contains("explanation", prompt);
        AssertPrompt(prompt[prompt.IndexOf('{')..]);
    }

    [Theory]
    [InlineData("chat", "")]
    [InlineData("chat", "codex_cli_rs/0.1.0")]
    [InlineData("responses", "")]
    [InlineData("responses", "codex_cli_rs/0.1.0")]
    public async Task User_agent_is_sent_only_when_configured(string wireApi, string userAgent)
    {
        await using var server = new FakeOpenAiServer(ResponsePayload(wireApi, AdviceJson));
        using var client = CreateHttpClient();
        var settings = CreateSettings(server, wireApi);
        settings.AiUserAgent = userAgent;
        var advisor = CreateAdvisor(client, settings);

        var advice = await advisor.ReviewAsync(_goal, _metrics, _candidates);

        AssertAdvice(advice);
        var request = Assert.Single(server.Requests);
        Assert.Equal(userAgent.Length == 0 ? null : userAgent, request.Headers["User-Agent"]);
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("responses")]
    public async Task Invalid_candidate_uses_rule_based_fallback(string wireApi)
    {
        var output = JsonSerializer.Serialize(new { candidateId = "not-allowed", explanation = Explanation });
        await using var server = new FakeOpenAiServer(ResponsePayload(wireApi, output));
        using var client = CreateHttpClient();
        var advisor = CreateAdvisor(client, CreateSettings(server, wireApi));

        var advice = await advisor.ReviewAsync(_goal, _metrics, _candidates);

        AssertFallback(advice);
        Assert.Single(server.Requests);
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("responses")]
    public async Task Empty_explanation_uses_rule_based_fallback(string wireApi)
    {
        var output = JsonSerializer.Serialize(new { candidateId = "balanced", explanation = "   " });
        await using var server = new FakeOpenAiServer(ResponsePayload(wireApi, output));
        using var client = CreateHttpClient();
        var advisor = CreateAdvisor(client, CreateSettings(server, wireApi));

        var advice = await advisor.ReviewAsync(_goal, _metrics, _candidates);

        AssertFallback(advice);
        Assert.Single(server.Requests);
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("responses")]
    public async Task Empty_model_uses_fallback_without_http_calls(string wireApi)
    {
        await using var server = new FakeOpenAiServer(ResponsePayload(wireApi, AdviceJson));
        using var client = CreateHttpClient();
        var settings = CreateSettings(server, wireApi);
        settings.AiModel = "";
        var advisor = CreateAdvisor(client, settings);

        var advice = await advisor.ReviewAsync(_goal, _metrics, _candidates);

        AssertFallback(advice);
        Assert.Empty(server.Requests);
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("responses")]
    public async Task Missing_key_uses_fallback_without_http_calls(string wireApi)
    {
        await using var server = new FakeOpenAiServer(ResponsePayload(wireApi, AdviceJson));
        using var client = CreateHttpClient();
        var advisor = CreateAdvisor(client, CreateSettings(server, wireApi), apiKey: null);

        var advice = await advisor.ReviewAsync(_goal, _metrics, _candidates);

        AssertFallback(advice);
        Assert.Empty(server.Requests);
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("responses")]
    public async Task Http_failure_uses_rule_based_fallback(string wireApi)
    {
        await using var server = new FakeOpenAiServer("""{"error":{"message":"local test failure"}}""", HttpStatusCode.BadRequest);
        using var client = CreateHttpClient();
        var advisor = CreateAdvisor(client, CreateSettings(server, wireApi));

        var advice = await advisor.ReviewAsync(_goal, _metrics, _candidates);

        AssertFallback(advice);
        Assert.Single(server.Requests);
    }

    private static HttpClient CreateHttpClient() =>
        new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };

    private static AppSettings CreateSettings(FakeOpenAiServer server, string wireApi) =>
        new() { AiEndpoint = server.Endpoint, AiModel = TestModel, AiWireApi = wireApi };

    private static OpenAiCompatibleAdvisor CreateAdvisor(HttpClient client, AppSettings settings, string? apiKey = TestApiKey)
    {
        var credentials = new TestCredentialStore();
        if (apiKey is not null)
        {
            credentials.Write(settings.AiApiKeyCredentialName, apiKey);
        }

        return new OpenAiCompatibleAdvisor(client, settings, credentials, new RuleBasedAiAdvisor());
    }

    private static string ResponsePayload(string wireApi, string output) =>
        wireApi == "chat"
            ? JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = output } } } })
            : JsonSerializer.Serialize(new
            {
                output = new[]
                {
                    new { type = "message", content = new[] { new { type = "output_text", text = output } } }
                }
            });

    private static void AssertAdvice(AiAdvice advice)
    {
        Assert.False(advice.UsedFallback);
        Assert.Equal("balanced", advice.CandidateId);
        Assert.Equal(Explanation, advice.Explanation);
    }

    private void AssertFallback(AiAdvice advice)
    {
        Assert.True(advice.UsedFallback);
        Assert.Equal(_candidates[0].Id, advice.CandidateId);
        Assert.False(string.IsNullOrWhiteSpace(advice.Explanation));
    }

    private void AssertPrompt(string prompt)
    {
        using var document = JsonDocument.Parse(prompt);
        var data = document.RootElement;
        Assert.Equal(_goal.TargetFps, data.GetProperty("goal").GetProperty("targetFps").GetDouble());
        Assert.Equal(_metrics.AverageFps, data.GetProperty("metrics").GetProperty("averageFps").GetDouble());
        var candidates = data.GetProperty("candidates");
        Assert.Equal(_candidates.Count, candidates.GetArrayLength());
        Assert.Equal("quality", candidates[0].GetProperty("id").GetString());
        Assert.Equal("balanced", candidates[1].GetProperty("id").GetString());
        Assert.Equal("1", candidates[1].GetProperty("settingOverrides").GetProperty("VolumetricQuality").GetString());
    }

    private sealed class TestCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);
        public string? Read(string name) => _secrets.GetValueOrDefault(name);
        public void Write(string name, string secret) => _secrets[name] = secret;
        public void Delete(string name) => _secrets.Remove(name);
    }

    private sealed record RecordedRequest(string Method, string Path, NameValueCollection Headers, JsonElement Body);

    private sealed class FakeOpenAiServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _shutdown = new();
        private readonly ConcurrentQueue<RecordedRequest> _requests = new();
        private readonly string _responseJson;
        private readonly HttpStatusCode _statusCode;
        private readonly Task _serverTask;

        public FakeOpenAiServer(string responseJson, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responseJson = responseJson;
            _statusCode = statusCode;
            var portReservation = new TcpListener(IPAddress.Loopback, 0);
            portReservation.Start();
            var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
            portReservation.Stop();
            var address = $"http://127.0.0.1:{port}/";
            Endpoint = address + "v1";
            _listener.Prefixes.Add(address);
            _listener.Start();
            _serverTask = Task.Run(ServeAsync);
        }

        public string Endpoint { get; }
        public IReadOnlyCollection<RecordedRequest> Requests => _requests.ToArray();

        private async Task ServeAsync()
        {
            try
            {
                while (!_shutdown.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync();
                    using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                    var body = await reader.ReadToEndAsync(_shutdown.Token);
                    using var document = JsonDocument.Parse(body);
                    _requests.Enqueue(new RecordedRequest(
                        context.Request.HttpMethod,
                        context.Request.Url!.AbsolutePath,
                        new NameValueCollection(context.Request.Headers),
                        document.RootElement.Clone()));
                    var response = Encoding.UTF8.GetBytes(_responseJson);
                    context.Response.StatusCode = (int)_statusCode;
                    context.Response.ContentType = "application/json; charset=utf-8";
                    context.Response.ContentLength64 = response.Length;
                    await context.Response.OutputStream.WriteAsync(response, _shutdown.Token);
                    context.Response.Close();
                }
            }
            catch (HttpListenerException) when (_shutdown.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested) { }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            _listener.Close();
            try
            {
                await _serverTask;
            }
            finally
            {
                _shutdown.Dispose();
            }
        }
    }
}
