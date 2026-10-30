using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GameTuner.Core.Mcp;

public interface IMcpClient : IAsyncDisposable
{
    Task EnsureInitializedAsync(CancellationToken cancellationToken = default);
    Task<McpToolResult> CallToolAsync(
        string name,
        JsonObject? arguments = null,
        CancellationToken cancellationToken = default);
}

public sealed class McpClient : IMcpClient
{
    private readonly string _executablePath;
    private readonly IReadOnlyList<string> _arguments;
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private Process? _process;
    private Task? _readerTask;
    private Task? _stderrTask;
    private long _nextId;
    private bool _initialized;
    private bool _disposed;

    public McpClient(
        string? executablePath = null,
        TimeSpan? requestTimeout = null,
        IReadOnlyList<string>? arguments = null)
    {
        _executablePath = executablePath ??
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "NVIDIA Corporation",
                "NVIDIA App",
                "McpServer",
                "NvAppMcpServer.exe");
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(20);
        _arguments = arguments ?? [];
    }

    public string ExecutablePath => _executablePath;

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _startLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            if (!File.Exists(_executablePath))
            {
                throw new FileNotFoundException(
                    "未找到 NVIDIA App MCP Bridge。请先安装并启动 NVIDIA App。",
                    _executablePath);
            }

            StartProcess();
            await SendRequestAsync(
                "initialize",
                new JsonObject
                {
                    ["protocolVersion"] = "2025-06-18",
                    ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject
                    {
                        ["name"] = "GameTuner",
                        ["version"] = "0.1.0"
                    }
                },
                cancellationToken);
            await SendNotificationAsync("notifications/initialized", new JsonObject(), cancellationToken);
            _initialized = true;
        }
        finally
        {
            _startLock.Release();
        }
    }

    public async Task<McpToolResult> CallToolAsync(
        string name,
        JsonObject? arguments = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await EnsureInitializedAsync(cancellationToken);
        var response = await SendRequestAsync(
            "tools/call",
            new JsonObject
            {
                ["name"] = name,
                ["arguments"] = arguments ?? new JsonObject()
            },
            cancellationToken);

        var result = response["result"] as JsonObject;
        if (result is null)
        {
            var error = response["error"] as JsonObject;
            throw new InvalidOperationException(
                error?["message"]?.GetValue<string>() ?? $"MCP 工具 {name} 没有返回结果。");
        }

        var content = result["content"] as JsonArray;
        var text = content?
            .Select(node => node?["text"]?.GetValue<string>())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
        var isError = result["isError"]?.GetValue<bool>() ?? false;
        var meta = result["_meta"] as JsonObject;

        return new McpToolResult
        {
            IsError = isError,
            Text = text,
            StructuredContent = result["structuredContent"]?.DeepClone(),
            ErrorCode = meta?["error_code"]?.GetValue<string>(),
            RawJson = result.ToJsonString()
        };
    }

    private void StartProcess()
    {
        var startInfo = new ProcessStartInfo(_executablePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in _arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 NVIDIA MCP Bridge。");
        _process.EnableRaisingEvents = true;
        _process.Exited += OnProcessExited;
        _readerTask = Task.Run(ReadLoopAsync);
        _stderrTask = Task.Run(ReadStderrLoopAsync);
    }

    private async Task<JsonObject> SendRequestAsync(
        string method,
        JsonObject parameters,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_process is null || _process.HasExited)
        {
            throw new InvalidOperationException("NVIDIA MCP Bridge 未运行。");
        }

        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonObject>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException("MCP 请求编号冲突。");
        }

        try
        {
            var message = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters
            };
            await _process.StandardInput.WriteLineAsync(message.ToJsonString(_jsonOptions));
            await _process.StandardInput.FlushAsync(cancellationToken);

            return await completion.Task.WaitAsync(_requestTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"MCP 请求 {method} 超过 {_requestTimeout.TotalSeconds:0} 秒未响应。");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task SendNotificationAsync(
        string method,
        JsonObject parameters,
        CancellationToken cancellationToken)
    {
        if (_process is null || _process.HasExited)
        {
            throw new InvalidOperationException("NVIDIA MCP Bridge 未运行。");
        }

        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters
        };
        await _process.StandardInput.WriteLineAsync(message.ToJsonString(_jsonOptions));
        await _process.StandardInput.FlushAsync(cancellationToken);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (_process is { HasExited: false })
            {
                var line = await _process.StandardOutput.ReadLineAsync();
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonObject? message;
                try
                {
                    message = JsonNode.Parse(line) as JsonObject;
                }
                catch (JsonException)
                {
                    continue;
                }

                if (message?["id"] is null)
                {
                    continue;
                }

                var id = message["id"]!.GetValue<long>();
                if (_pending.TryRemove(id, out var completion))
                {
                    completion.TrySetResult(message);
                }
            }
        }
        catch (Exception exception)
        {
            FailPending(exception);
        }
        finally
        {
            FailPending(new InvalidOperationException("NVIDIA MCP Bridge 已停止。"));
        }
    }

    private async Task ReadStderrLoopAsync()
    {
        try
        {
            while (_process is { HasExited: false })
            {
                var line = await _process.StandardError.ReadLineAsync();
                if (line is null)
                {
                    break;
                }

                Debug.WriteLine($"[NvMcp] {line}");
            }
        }
        catch
        {
            // Diagnostics only.
        }
    }

    private void OnProcessExited(object? sender, EventArgs args) =>
        FailPending(new InvalidOperationException("NVIDIA MCP Bridge 意外退出。"));

    private void FailPending(Exception exception)
    {
        foreach (var pair in _pending)
        {
            if (_pending.TryRemove(pair.Key, out var completion))
            {
                completion.TrySetException(exception);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _initialized = false;
        FailPending(new ObjectDisposedException(nameof(McpClient)));

        if (_process is { HasExited: false })
        {
            try
            {
                _process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _process.WaitForExitAsync(timeout.Token);
            }
            catch
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Process already exited.
                }
            }
        }

        _process?.Dispose();
        _startLock.Dispose();
    }
}
