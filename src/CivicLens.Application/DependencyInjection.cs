using CivicLens.Application.Abstractions;
using CivicLens.Application.Demo;
using CivicLens.Application.Dtos;
using CivicLens.Application.Services;
using CivicLens.Domain.Configuration;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CivicLens.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddCivicLensApplication(this IServiceCollection s, IConfiguration config)
    {
        s.Configure<ScoringOptions>(config.GetSection("CivicLens:Scoring"));

        s.AddScoped<IContextRiskService, ContextRiskService>();
        s.AddScoped<IIncidentMatchingService, IncidentMatchingService>();
        s.AddScoped<IPriorityScoringService, PriorityScoringService>();
        s.AddScoped<RecurrenceService>();
        s.AddScoped<CorrelationService>();
        s.AddScoped<TrustService>();
        s.AddScoped<IncidentLifecycle>();
        s.AddScoped<ObservationService>();
        s.AddScoped<IncidentService>();
        s.AddScoped<ResolutionService>();
        s.AddScoped<DashboardService>();
        s.AddScoped<PoiCoverageService>();
        s.AddScoped<AdminService>();
        s.AddScoped<AuthService>();
        s.AddScoped<UserService>();
        s.AddScoped<Abstractions.INotificationService, InAppNotificationService>();
        s.AddScoped<DemoSeeder>();
        s.AddScoped<DemoService>();

        s.AddValidatorsFromAssemblyContaining<RegisterRequestValidator>();
        return s;
    }
}

public sealed class AssignRequestValidator : AbstractValidator<AssignRequest>
{
    public AssignRequestValidator()
    {
        RuleFor(x => x.WorkerId).NotEmpty().WithMessage("Choose a field worker.");
        RuleFor(x => x.Note).MaximumLength(300).WithMessage("The note must be 300 characters or fewer.");
    }
}

public sealed class StatusChangeRequestValidator : AbstractValidator<StatusChangeRequest>
{
    public StatusChangeRequestValidator()
    {
        RuleFor(x => x.Status).NotEmpty().WithMessage("Status is required.");
        RuleFor(x => x.Reason).MaximumLength(500).WithMessage("The reason must be 500 characters or fewer.");
    }
}

public sealed class OverrideRequestValidator : AbstractValidator<OverrideRequest>
{
    public OverrideRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500).WithMessage("A reason (up to 500 characters) is required for an override.");
        RuleFor(x => x.Severity).InclusiveBetween(0, 100).When(x => x.Severity.HasValue).WithMessage("Severity must be between 0 and 100.");
    }
}

public sealed class VoteRequestValidator : AbstractValidator<VoteRequest>
{
    public VoteRequestValidator()
    {
        RuleFor(x => x.Vote).NotEmpty().WithMessage("Choose Fixed, StillExists or Unsure.");
        RuleFor(x => x.Latitude).InclusiveBetween(-90, 90).When(x => x.Latitude.HasValue);
        RuleFor(x => x.Longitude).InclusiveBetween(-180, 180).When(x => x.Longitude.HasValue);
    }
}

public sealed class SubmitObservationRequestValidator : AbstractValidator<SubmitObservationRequest>
{
    public SubmitObservationRequestValidator()
    {
        RuleFor(x => x.DraftId).NotEmpty().WithMessage("Analyze a photo first.");
        RuleFor(x => x.Action).NotEmpty().WithMessage("Choose whether to create a new incident or attach to an existing one.");
        RuleFor(x => x.IncidentId).NotEmpty().When(x => string.Equals(x.Action, "AttachToExisting", StringComparison.OrdinalIgnoreCase))
            .WithMessage("Choose the incident to attach this observation to.");
        RuleFor(x => x.Description).MaximumLength(500).WithMessage("The description must be 500 characters or fewer.");
    }
}
