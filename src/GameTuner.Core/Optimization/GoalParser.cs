using System.Globalization;
using System.Text.RegularExpressions;
using GameTuner.Core.Models;

namespace GameTuner.Core.Optimization;

public static partial class GoalParser
{
    public static TuningGoal Parse(string text, double fallbackFps = 60)
    {
        var goal = new TuningGoal { TargetFps = fallbackFps };
        if (string.IsNullOrWhiteSpace(text))
        {
            return goal;
        }

        var fps = FpsPattern().Match(text);
        if (fps.Success &&
            double.TryParse(fps.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            goal.TargetFps = parsed;
        }

        if (text.Contains("画质", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("质量", StringComparison.OrdinalIgnoreCase))
        {
            goal.Mode = TuningMode.Quality;
        }
        else if (text.Contains("帧数", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("性能", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("流畅", StringComparison.OrdinalIgnoreCase))
        {
            goal.Mode = TuningMode.FrameRate;
        }
        else if (text.Contains("平衡", StringComparison.OrdinalIgnoreCase))
        {
            goal.Mode = TuningMode.Balanced;
        }

        goal.Validate();
        return goal;
    }

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*(?:fps|帧)", RegexOptions.IgnoreCase)]
    private static partial Regex FpsPattern();
}
