using CivicLens.Application.Abstractions;
using CivicLens.Application.Common;
using CivicLens.Domain;
using CivicLens.Domain.Entities;

namespace CivicLens.Application.Services;

/// <summary>Single place that mutates incident status: enforces the state machine and writes the audit trail.</summary>
public sealed class IncidentLifecycle(IAppDbContext db, TimeProvider clock)
{
    public DateTime Now => clock.GetUtcNow().UtcDateTime;

    public IncidentHistory AddEvent(Guid incidentId, string eventType, string message, Guid? actorId,
        string? metadata = null, IncidentStatus? oldStatus = null, IncidentStatus? newStatus = null)
    {
        var h = new IncidentHistory
        {
            IncidentId = incidentId, EventType = eventType, Message = message, ActorId = actorId, Metadata = metadata,
            OldStatus = oldStatus, NewStatus = newStatus, CreatedAt = Now
        };
        db.History.Add(h);
        return h;
    }

    /// <summary>Validates and applies a transition. Throws <see cref="InvalidTransitionException"/> for illegal moves.</summary>
    public void Transition(CivicIncident incident, IncidentStatus to, TransitionActor actor, Guid? actorId, string message, string? metadata = null)
    {
        var from = incident.Status;
        if (!IncidentStateMachine.IsAllowed(from, to))
            throw new InvalidTransitionException($"An incident cannot move from {from} to {to}.");
        if (!IncidentStateMachine.IsAllowed(from, to, actor))
            throw new ForbiddenException($"A {actor} is not allowed to move an incident from {from} to {to}.");

        incident.Status = to;
        incident.ResolutionStatus = IncidentStateMachine.ResolutionFor(to);
        incident.UpdatedAt = Now;
        if (to == IncidentStatus.VerifiedResolved) incident.ResolvedAt = Now;
        if (to == IncidentStatus.Reopened)
        {
            incident.ResolvedAt = null;
            incident.ReopenCount++;
        }
        AddEvent(incident.Id, "StatusChanged", message, actorId, metadata, from, to);
    }
}
