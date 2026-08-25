using FluentValidation;
using Microsoft.EntityFrameworkCore;
using ProfileSvr.Common;
using ProfileSvr.Database;
using ProfileSvr.Domain;

namespace ProfileSvr.Features.Profiles;

public static class SetPhoneNumber
{
    public record Request(string PhoneNumber, Guid DeviceId);

    private record Response(Guid Id, string PhoneNumber, bool PhoneNumberConfirmed, DateTime? UpdatedAtUtc);

    public class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(r => r.DeviceId).NotEmpty();
            RuleFor(r => r.PhoneNumber)
                .NotEmpty()
                .MaximumLength(32)
                .Matches(@"^\+?[0-9\s\-()]{7,32}$")
                .WithMessage("Phone number must contain 7-32 digits and may include +, spaces, dashes or parentheses.");
        }
    }

    public class Endpoint : IEndpoint
    {
        public static void Map(IEndpointRouteBuilder app) =>
            app.MapPut("/api/profiles/{id:guid}/phone", Handle)
                .WithSummary("Set or replace the profile's phone number (resets confirmation)")
                .WithTags("Profiles");
    }

    private static async Task<IResult> Handle(
        Guid id,
        Request request,
        AppDbContext db,
        IValidator<Request> validator,
        CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return ApiResults.ValidationFailed(validation);

        var profile = await db.Profiles.FindAsync([id], ct);
        if (profile is null)
            return ApiResults.NotFound("Profile not found.");

        var phone = request.PhoneNumber.Trim();

        var taken = await db.Profiles.AnyAsync(p => p.Id != id && p.PhoneNumber == phone, ct);
        if (taken)
            return ApiResults.Conflict("Another profile already uses this phone number.");

        profile.PhoneNumber = phone;
        profile.PhoneNumberConfirmed = false;
        profile.UpdatedAtUtc = DateTime.UtcNow;
        ActivityLog.Record(db, ActivityType.PhoneNumberSet, profile.Id, request.DeviceId, $"Phone number {phone} set.");
        await db.SaveChangesAsync(ct);

        return ApiResults.Ok(new Response(profile.Id, phone, profile.PhoneNumberConfirmed, profile.UpdatedAtUtc));
    }
}
