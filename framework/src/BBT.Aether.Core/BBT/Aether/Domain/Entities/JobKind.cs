namespace BBT.Aether.Domain.Entities;

/// <summary>
/// Whether a background job fires once at a due time (<see cref="OneShot"/>) or repeats on a
/// schedule (<see cref="Recurring"/>, e.g. a cron expression).
/// </summary>
public enum JobKind
{
    OneShot = 0,
    Recurring = 1,
}
