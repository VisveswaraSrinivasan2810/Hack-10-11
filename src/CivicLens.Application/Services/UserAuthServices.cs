using System.Net.Mail;
using CivicLens.Application.Abstractions;
using CivicLens.Application.Common;
using CivicLens.Application.Dtos;
using CivicLens.Domain;
using CivicLens.Domain.Entities;
using CivicLens.Domain.Scoring;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CivicLens.Application.Services;

public sealed class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(80).WithMessage("Please enter your name (up to 80 characters).");
        RuleFor(x => x.Email).NotEmpty().MaximumLength(160).Must(BeEmail).WithMessage("Please enter a valid email address.");
        RuleFor(x => x.Password).Must(PasswordRules.IsStrong).WithMessage(PasswordRules.Message);
    }
    private static bool BeEmail(string e) => MailAddress.TryCreate(e, out var a) && a.Address == e;
}

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().WithMessage("Email is required.");
        RuleFor(x => x.Password).NotEmpty().WithMessage("Password is required.");
    }
}

public sealed class AuthService(IAppDbContext db, IPasswordHasher hasher, ITokenService tokens, TimeProvider clock)
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockWindow = TimeSpan.FromMinutes(15);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Count, DateTime First)> Failures = new();

    public async Task<AuthResponse> RegisterAsync(RegisterRequest req, CancellationToken ct = default)
    {
        var email = req.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            throw new ConflictException("An account with this email already exists.");
        // Self-registration always creates a citizen; authority/worker accounts are provisioned by administrators.
        var user = new User
        {
            Name = req.Name.Trim(), Email = email, PasswordHash = hasher.Hash(req.Password), Role = UserRole.Citizen,
            CreatedAt = clock.GetUtcNow().UtcDateTime
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return Issue(user);
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest req, CancellationToken ct = default)
    {
        var email = req.Email.Trim().ToLowerInvariant();
        var now = clock.GetUtcNow().UtcDateTime;
        if (Failures.TryGetValue(email, out var f) && now - f.First < LockWindow && f.Count >= MaxFailures)
            throw new RateLimitException("Too many failed sign-in attempts. Please wait 15 minutes and try again.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is null || !hasher.Verify(req.Password, user.PasswordHash))
        {
            Failures.AddOrUpdate(email, (1, now), (_, old) => now - old.First >= LockWindow ? (1, now) : (old.Count + 1, old.First));
            throw new UnauthorizedAppException("Incorrect email or password.");
        }
        if (!user.IsActive) throw new ForbiddenException("This account has been deactivated. Please contact your administrator.");
        Failures.TryRemove(email, out _);
        return Issue(user);
    }

    private AuthResponse Issue(User user)
    {
        var (token, exp) = tokens.Create(user);
        return new AuthResponse(token, exp, ToDto(user));
    }

    public static UserDto ToDto(User u) => new(u.Id, u.Name, u.Email, u.Role.ToString(), u.TrustScore);
}

public sealed class UserService(IAppDbContext db, TrustService trust)
{
    public async Task<UserDto> MeAsync(Guid userId, CancellationToken ct = default) =>
        AuthService.ToDto(await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw new NotFoundException("User not found."));

    public async Task<IReadOnlyList<ContributionItemDto>> ContributionsAsync(Guid userId, CancellationToken ct = default)
    {
        var observations = await (from io in db.IncidentObservations
                                  join o in db.Observations on io.ObservationId equals o.Id
                                  join i in db.Incidents on io.IncidentId equals i.Id
                                  where o.UserId == userId
                                  select new ContributionItemDto("Observation", i.Id, i.PublicIncidentCode, i.Category.ToString(), i.Status.ToString(),
                                      o.CreatedAt, o.ImageUrl, o.ValidationStatus.ToString())).AsNoTracking().ToListAsync(ct);
        var confirmations = await (from c in db.IncidentConfirmations
                                   join i in db.Incidents on c.IncidentId equals i.Id
                                   where c.UserId == userId
                                   select new ContributionItemDto("Confirmation", i.Id, i.PublicIncidentCode, i.Category.ToString(), i.Status.ToString(),
                                       c.CreatedAt, i.PrimaryImageUrl, null)).AsNoTracking().ToListAsync(ct);
        var votes = await (from v in db.CitizenVerifications
                           join i in db.Incidents on v.IncidentId equals i.Id
                           where v.UserId == userId
                           select new ContributionItemDto("ResolutionCheck", i.Id, i.PublicIncidentCode, i.Category.ToString(), i.Status.ToString(),
                               v.CreatedAt, i.PrimaryImageUrl, v.Vote.ToString())).AsNoTracking().ToListAsync(ct);

        return observations.Concat(confirmations).Concat(votes)
            .Select(c => c with { CategoryLabel = CategoryRules.Label(Enum.Parse<IssueCategory>(c.CategoryLabel)) })
            .OrderByDescending(c => c.At).ToList();
    }

    public async Task<TrustDto> TrustAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw new NotFoundException("User not found.");
        var r = await trust.CalculateAsync(userId, ct);
        if (user.TrustScore != r.Score) { user.TrustScore = r.Score; await db.SaveChangesAsync(ct); }
        var total = await db.Observations.CountAsync(o => o.UserId == userId, ct);
        var level = r.Score >= 80 ? "Trusted contributor" : r.Score >= 55 ? "Reliable contributor" : "New contributor";
        return new TrustDto(r.Score, r.Inputs.VerifiedObservations, r.Inputs.AccurateClassifications, r.Inputs.UsefulConfirmations,
            r.Inputs.ResolutionChecks, r.Inputs.InvalidSubmissions, total, r.Factors, level);
    }


    public async Task ChangePasswordAsync(Guid userId, string current, string next, IPasswordHasher hasher, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw new NotFoundException("User not found.");
        if (!hasher.Verify(current, user.PasswordHash)) throw new BadRequestException("Your current password is incorrect.", "wrong_password");
        user.PasswordHash = hasher.Hash(next);
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await db.SaveChangesAsync(ct);
    }
    public async Task<IReadOnlyList<NotificationDto>> NotificationsAsync(Guid userId, CancellationToken ct = default) =>
        await db.Notifications.AsNoTracking().Where(n => n.UserId == userId).OrderByDescending(n => n.CreatedAt).Take(50)
            .Select(n => new NotificationDto(n.Id, n.EventType, n.Title, n.Message, n.IsRead, n.CreatedAt, n.IncidentId)).ToListAsync(ct);

    public async Task MarkNotificationsReadAsync(Guid userId, CancellationToken ct = default)
    {
        foreach (var n in await db.Notifications.Where(n => n.UserId == userId && !n.IsRead).ToListAsync(ct)) n.IsRead = true;
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>In-app notification channel (MVP). Replace with push/SMS behind the same interface.</summary>
public sealed class InAppNotificationService(IAppDbContext db, TimeProvider clock) : INotificationService
{
    public async Task NotifyAsync(Guid userId, string eventType, string title, string message, Guid? incidentId, CancellationToken ct = default)
    {
        db.Notifications.Add(new Notification
        { UserId = userId, EventType = eventType, Title = title, Message = message, IncidentId = incidentId, CreatedAt = clock.GetUtcNow().UtcDateTime });
        await db.SaveChangesAsync(ct);
    }

    public async Task NotifyRoleAsync(UserRole role, string eventType, string title, string message, Guid? incidentId, CancellationToken ct = default)
    {
        var ids = await db.Users.Where(u => u.Role == role).Select(u => u.Id).ToListAsync(ct);
        foreach (var id in ids)
            db.Notifications.Add(new Notification
            { UserId = id, EventType = eventType, Title = title, Message = message, IncidentId = incidentId, CreatedAt = clock.GetUtcNow().UtcDateTime });
        await db.SaveChangesAsync(ct);
    }
}
