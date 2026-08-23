namespace DekhBhai.Core.Session;

/// <summary>
/// The four durations the product offers. The wire id (used with the signaling server and
/// matching signaling/src/sessionDuration.js exactly) is looked up via <see cref="WireId"/>
/// rather than relying on enum-to-string casing, so the two sides can never drift silently.
/// </summary>
public enum SessionDuration
{
    FifteenMinutes,
    OneHour,
    FiveHours,
    UntilStopped,
}

public static class SessionDurationExtensions
{
    public static string WireId(this SessionDuration duration) => duration switch
    {
        SessionDuration.FifteenMinutes => "fifteenMinutes",
        SessionDuration.OneHour => "oneHour",
        SessionDuration.FiveHours => "fiveHours",
        SessionDuration.UntilStopped => "untilStopped",
        _ => throw new ArgumentOutOfRangeException(nameof(duration)),
    };

    public static string DisplayName(this SessionDuration duration) => duration switch
    {
        SessionDuration.FifteenMinutes => "15 Minutes",
        SessionDuration.OneHour => "1 Hour",
        SessionDuration.FiveHours => "5 Hours",
        SessionDuration.UntilStopped => "Until I Stop",
        _ => throw new ArgumentOutOfRangeException(nameof(duration)),
    };

    /// <summary>Total duration, or null for <see cref="SessionDuration.UntilStopped"/> (no cap).</summary>
    public static TimeSpan? ToTimeSpan(this SessionDuration duration) => duration switch
    {
        SessionDuration.FifteenMinutes => TimeSpan.FromMinutes(15),
        SessionDuration.OneHour => TimeSpan.FromHours(1),
        SessionDuration.FiveHours => TimeSpan.FromHours(5),
        SessionDuration.UntilStopped => null,
        _ => throw new ArgumentOutOfRangeException(nameof(duration)),
    };
}
