namespace GameTuner.App.Processes;

/// <summary>A running process reduced to the fields the picker and detection read.</summary>
public sealed record GameProcessInfo(int Id, string ProcessName, string WindowTitle);

/// <summary>
/// A detection candidate plus how much of the primary screen its window covers. A game
/// the player wants to capture is almost always full screen, while desktop apps and
/// overlays are windowed; that ratio separates them without shipping a game catalogue.
/// </summary>
public sealed record GameWindowCandidate(int Id, string ProcessName, string WindowTitle, double ScreenCoverage);