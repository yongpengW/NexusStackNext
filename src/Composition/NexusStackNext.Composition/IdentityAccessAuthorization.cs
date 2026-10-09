using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Domain.Authorization;
using NexusStackNext.Identity.Contracts;

namespace NexusStackNext.Composition;

internal sealed class HttpRequestAccessValidator(IdentityAuthorityReader authority, IdentitySessionClientOptions options) : IRequestAccessValidator
{
    public async Task<Result<bool>> ValidateAsync(string userId, long? sessionVersion, PermissionKey required, CancellationToken cancellationToken = default)
    {
        var endpoint = new Uri(options.Endpoint(), "/api/identity/access/v1?permissionKey=" + Uri.EscapeDataString(required.Value));
        var response = await authority.ReadAsync<CurrentAccessV1>(endpoint, sessionVersion, cancellationToken).ConfigureAwait(false);
        if (response.IsFailure) { return Result.Failure<bool>(response.Error); }
        var decision = response.Value;
        return decision.ContractVersion == 1 && decision.Subject == userId && decision.SessionVersion == sessionVersion && decision.PermissionKey == required.Value
            ? Result.Success(decision.IsAllowed) : Result.Failure<bool>(SessionValidationErrors.Unavailable);
    }
}
