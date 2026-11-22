using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;

namespace RetailCloud.IdentityAccess.Application;

/// <summary>D07 <c>RoleAssignment</c>.</summary>
public sealed record AssignmentView(Guid Id, Guid RoleId, string RoleName, string ScopeType, Guid? ScopeId)
{
    public RoleGrant ToGrant() => new(RoleName, Enum.Parse<ScopeType>(ScopeType), ScopeId);
}

/// <summary>D07 <c>User</c>.</summary>
public sealed record UserView(Guid Id, string DisplayName, string Status, IReadOnlyList<AssignmentView> RoleAssignments, DateTimeOffset? LastLoginAt);

/// <summary>D07 <c>Invitation</c>. <see cref="InviteToken"/> is returned once, at creation; only its hash is stored.</summary>
public sealed record InvitationView(Guid Id, string DisplayName, string Status, DateTimeOffset ExpiresAt,
    IReadOnlyList<RoleGrant> RoleAssignments, string? InviteToken);

public sealed record AcceptedInvitation(Guid TenantId, string TenantName, IReadOnlyList<RoleGrant> RoleAssignments);

/// <summary>US-025 (IAM-001, IAM-006). Authorization (who may do what at which scope) is decided by the caller.</summary>
public interface IUserAdministration
{
    Task<IReadOnlyList<UserView>> ListAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken);

    Task<InvitationView> InviteAsync(IUnitOfWorkScope scope, UserId invitedBy, string displayName, string? email, string? mobile,
        IReadOnlyList<RoleGrant> grants, CancellationToken cancellationToken);

    /// <summary>Tenant of a pending, unexpired invitation (no tenant context needed), or null.</summary>
    Task<TenantId?> FindInvitationTenantAsync(string token, CancellationToken cancellationToken);

    /// <summary>In the invitation's tenant context: membership + assignments, invitation marked accepted. Null if no longer pending.</summary>
    Task<AcceptedInvitation?> AcceptAsync(IUnitOfWorkScope scope, string token, UserId user, CancellationToken cancellationToken);

    Task<AssignmentView?> GetAssignmentAsync(IUnitOfWorkScope scope, UserId user, Guid assignmentId, CancellationToken cancellationToken);

    /// <summary>Null when the user is not a member of the tenant; throws <see cref="DuplicateAssignmentException"/>.</summary>
    Task<AssignmentView?> AddAssignmentAsync(IUnitOfWorkScope scope, UserId user, RoleGrant grant, UserId assignedBy, CancellationToken cancellationToken);

    /// <summary>Throws <see cref="LastOwnerException"/> when it would leave the tenant without a TenantOwner.</summary>
    Task RemoveAssignmentAsync(IUnitOfWorkScope scope, UserId user, Guid assignmentId, CancellationToken cancellationToken);
}

public sealed class DuplicateAssignmentException : Exception
{
    public DuplicateAssignmentException()
        : base("The user already has this role at this scope.")
    {
    }
}

public sealed class LastOwnerException : Exception
{
    public LastOwnerException()
        : base("A tenant must keep at least one TenantOwner.")
    {
    }
}
