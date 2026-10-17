namespace PCDoctor.Core;

/// <summary>Turns measurements into a 0-100 "how much is this holding the game back" score for the race view.</summary>
public static class RaceScore
{
    /// <summary>The network's score from a ping: 0 up to 20 ms, 100 from 200 ms or when the ping got no answer.</summary>
    public static double NetworkLag(double? pingMs, bool lost) =>
        lost ? 100 : pingMs is null ? 0 : Math.Clamp((pingMs.Value - 20) / 180 * 100, 0, 100);
}
