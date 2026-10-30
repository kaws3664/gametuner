using System.Text.Json.Nodes;
using GameTuner.Core.Models;

namespace GameTuner.Core.Mcp;

public sealed record NvidiaApplication(
    string Name,
    bool OptimizationSupported,
    IReadOnlyList<QualityPreset> AvailablePresets);

public interface INvidiaAppService
{
    Task<IReadOnlyList<NvidiaApplication>> GetApplicationsAsync(
        string? game = null,
        CancellationToken cancellationToken = default);
    Task SetGamePresetAsync(
        string game,
        QualityPreset preset,
        CancellationToken cancellationToken = default);
    Task LaunchGameAsync(string game, CancellationToken cancellationToken = default);
    Task<bool> IsGameRunningAsync(CancellationToken cancellationToken = default);
    Task<bool> IsOverlayAvailableAsync(CancellationToken cancellationToken = default);
}

public sealed class NvidiaAppService(IMcpClient client) : INvidiaAppService
{
    public async Task<IReadOnlyList<NvidiaApplication>> GetApplicationsAsync(
        string? game = null,
        CancellationToken cancellationToken = default)
    {
        var arguments = new JsonObject();
        if (!string.IsNullOrWhiteSpace(game))
        {
            arguments["game"] = game;
        }

        var result = await client.CallToolAsync(
            "nvapp_client_get_applications",
            arguments,
            cancellationToken);
        ThrowIfToolError(result);

        var applications = result.StructuredContent?["applications"] as JsonArray;
        if (applications is null)
        {
            return [];
        }

        var parsed = new List<NvidiaApplication>();
        foreach (var node in applications.OfType<JsonObject>())
        {
            var name = node["game"]?.GetValue<string>()
                ?? node["displayName"]?.GetValue<string>()
                ?? "";
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var profiles = node["optimizationProfiles"] as JsonObject;
            var ac = profiles?["ac"] as JsonObject;
            var presets = new List<QualityPreset>();
            if (ac?["availablePresets"] is JsonArray available)
            {
                foreach (var value in available)
                {
                    if (value is not null &&
                        Enum.TryParse<QualityPreset>(value.GetValue<string>(), true, out var preset))
                    {
                        presets.Add(preset);
                    }
                }
            }

            parsed.Add(new NvidiaApplication(
                name,
                node["opsSupported"]?.GetValue<bool>() ?? false,
                presets));
        }

        return parsed;
    }

    public async Task SetGamePresetAsync(
        string game,
        QualityPreset preset,
        CancellationToken cancellationToken = default)
    {
        var arguments = new JsonObject
        {
            ["game"] = game,
            ["preset"] = preset.ToString(),
            ["powerSource"] = "AC"
        };
        var result = await client.CallToolAsync(
            "nvapp_client_set_game_optimization",
            arguments,
            cancellationToken);
        ThrowIfToolError(result);
    }

    public async Task LaunchGameAsync(string game, CancellationToken cancellationToken = default)
    {
        var result = await client.CallToolAsync(
            "nvapp_client_launch_application",
            new JsonObject { ["game"] = game },
            cancellationToken);
        ThrowIfToolError(result);
    }

    public async Task<bool> IsGameRunningAsync(CancellationToken cancellationToken = default)
    {
        var result = await client.CallToolAsync(
            "nvapp_overlay_get_current_game_info",
            null,
            cancellationToken);
        if (result.IsError)
        {
            return false;
        }

        return result.StructuredContent?["running"]?.GetValue<bool>() ?? false;
    }

    public async Task<bool> IsOverlayAvailableAsync(CancellationToken cancellationToken = default)
    {
        var result = await client.CallToolAsync(
            "nvapp_overlay_get_status",
            null,
            cancellationToken);
        return !result.IsError;
    }

    public static void ThrowIfToolError(McpToolResult result)
    {
        if (!result.IsError)
        {
            return;
        }

        var suffix = string.IsNullOrWhiteSpace(result.ErrorCode)
            ? ""
            : $" ({result.ErrorCode})";
        throw new InvalidOperationException(result.Text + suffix);
    }
}
