using static CivicLens.Domain.IncidentStatus;
using static CivicLens.Domain.TransitionActor;

namespace CivicLens.Domain;

/// <summary>
/// Explicit incident state machine. No arbitrary jumps: every (from, to) pair lists which actors may perform it.
/// The system alone moves incidents through verification so "closed" can never be self-declared.
/// </summary>
public static class IncidentStateMachine
{
    private static readonly Dictionary<(IncidentStatus From, IncidentStatus To), TransitionActor[]> Rules = new()
    {
        [(Detected, Reported)] = [TransitionActor.System],
        [(Detected, Rejected)] = [Authority],
        [(Reported, Validated)] = [Authority],
        [(Reported, Assigned)] = [Authority],
        [(Reported, Rejected)] = [Authority],
        [(Validated, Assigned)] = [Authority],
        [(Validated, Rejected)] = [Authority],
        [(Assigned, Accepted)] = [Worker],
        [(Assigned, Validated)] = [Authority],
        [(Accepted, InProgress)] = [Worker],
        [(Accepted, Assigned)] = [Authority],
        [(InProgress, Assigned)] = [Authority],
        [(InProgress, WorkCompleted)] = [Worker, TransitionActor.System],
        [(WorkCompleted, AwaitingVerification)] = [TransitionActor.System],
        [(AwaitingVerification, VerifiedResolved)] = [TransitionActor.System, Authority],
        [(AwaitingVerification, VerificationFailed)] = [TransitionActor.System],
        [(VerificationFailed, RequiresReview)] = [TransitionActor.System],
        [(RequiresReview, Assigned)] = [Authority],
        [(RequiresReview, VerifiedResolved)] = [Authority],
        [(RequiresReview, Reopened)] = [Authority],
        [(VerifiedResolved, Reopened)] = [Authority, TransitionActor.System],
        [(Reopened, Assigned)] = [Authority],
        [(Reopened, Validated)] = [Authority],
        [(Rejected, Reopened)] = [Authority],
    };

    public static bool IsAllowed(IncidentStatus from, IncidentStatus to) => Rules.ContainsKey((from, to));

    public static bool IsAllowed(IncidentStatus from, IncidentStatus to, TransitionActor actor) =>
        Rules.TryGetValue((from, to), out var actors) && actors.Contains(actor);

    public static IReadOnlyList<IncidentStatus> NextStates(IncidentStatus from, TransitionActor? actor = null) =>
        Rules.Where(r => r.Key.From == from && (actor is null || r.Value.Contains(actor.Value)))
             .Select(r => r.Key.To).ToList();

    /// <summary>Statuses that count as "operationally open" for the authority queue.</summary>
    public static ResolutionStatus ResolutionFor(IncidentStatus s) => s switch
    {
        AwaitingVerification or WorkCompleted => ResolutionStatus.AwaitingVerification,
        VerifiedResolved => ResolutionStatus.VerifiedResolved,
        VerificationFailed or RequiresReview => ResolutionStatus.Disputed,
        _ => ResolutionStatus.None
    };
}
