namespace ConnectionClue.Core;

/// <summary>
/// How long before a symptom marker a matching problem may have happened. Extended serves users who need more
/// time to react (motor or cognitive disabilities). Snapshotted in the session profile so analysis and comparisons agree.
/// </summary>
public enum MarkerAllowance { Standard, Extended }

public static class MarkerWindow
{
    public static (TimeSpan Before, TimeSpan After) For(MarkerAllowance allowance) => allowance switch
    {
        MarkerAllowance.Extended => (TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(5)),
        _ => (TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5)),
    };
}
