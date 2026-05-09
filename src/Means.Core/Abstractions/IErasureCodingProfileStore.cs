namespace Means.Core;

public interface IErasureCodingProfileStore
{
    Task<IReadOnlyList<ErasureCodingProfile>> ListErasureCodingProfilesAsync(CancellationToken cancellationToken);

    Task<ErasureCodingProfile?> GetErasureCodingProfileAsync(string profileId, CancellationToken cancellationToken);

    Task<ErasureCodingProfile> SaveErasureCodingProfileAsync(ErasureCodingProfile profile, CancellationToken cancellationToken);

    Task DeleteErasureCodingProfileAsync(string profileId, CancellationToken cancellationToken);
}
