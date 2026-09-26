using ConnectionClue.Analysis;

namespace ConnectionClue.Presentation.Results;

/// <summary>
/// The last check that found issues or missing best practices, saved so its recommendations survive a restart. Stores
/// codes and values, never rendered text, so it re-renders in the current language. Plain JSON, not encrypted (by design).
/// Advisories is null in files written before best practices existed; StartedAtUtc (when sampling began, the origin of
/// verdict times) and Verdicts are null in files written before verdicts existed.
/// </summary>
public sealed record SavedResult(DateTimeOffset CheckedAtUtc, HealthReport Report, CheckContext Context, IReadOnlyList<ActionCode> Actions,
    IReadOnlyList<Advisory>? Advisories = null, IReadOnlyList<CheckArea>? Passed = null, DateTimeOffset? StartedAtUtc = null,
    IReadOnlyList<Verdict>? Verdicts = null);

public interface IResultStore
{
    SavedResult? Load();
    void Save(SavedResult result);
    void Clear();
}
