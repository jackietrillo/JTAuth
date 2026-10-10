using JTAuth.BuildingBlocks;
using JTAuth.BuildingBlocks.Handlers;
using JTAuth.Contracts;

namespace JTAuth.Application.Profile;

/// <summary>The signed-in person's display name and sign-in methods.</summary>
public sealed record GetProfileQuery(Guid UserId) : IQuery<ProfileDto>;

public sealed class GetProfileHandler(IUserRepository users) : IQueryHandler<GetProfileQuery, ProfileDto>
{
    public Task<Result<ProfileDto>> HandleAsync(GetProfileQuery query, CancellationToken cancellationToken) =>
        ProfileMapping.LoadAsync(users, query.UserId, cancellationToken);
}
