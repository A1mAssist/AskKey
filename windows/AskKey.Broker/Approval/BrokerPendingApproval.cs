namespace AskKey.Broker.Approval;

/// <summary>App-only projection used to render the direct confirmation surface. It
/// carries request metadata and the opaque decision capability, never credential values.</summary>
public sealed class BrokerPendingApproval : IEquatable<BrokerPendingApproval>
{
    public string RequestId { get; }
    public string Capability { get; }
    public BrokerApprovalOperationRequest Request { get; }
    public DateTimeOffset? ExpiresAt { get; }
    /// <summary>Vault-derived display metadata; never part of the operation binding.</summary>
    public string? TrustedCredentialName { get; }

    public string DisplayCredentialName => TrustedCredentialName ?? Request.CredentialName ?? Request.TargetId;

    public BrokerPendingApproval(
        string requestId,
        string capability,
        BrokerApprovalOperationRequest request,
        DateTimeOffset? expiresAt = null,
        string? trustedCredentialName = null)
    {
        RequestId = requestId;
        Capability = capability;
        Request = request;
        ExpiresAt = expiresAt;
        TrustedCredentialName = trustedCredentialName;
    }

    public bool Equals(BrokerPendingApproval? other)
        => other is not null
            && RequestId == other.RequestId
            && Capability == other.Capability
            && Request == other.Request
            && Nullable.Equals(ExpiresAt, other.ExpiresAt)
            && TrustedCredentialName == other.TrustedCredentialName;

    public override bool Equals(object? obj) => Equals(obj as BrokerPendingApproval);

    public override int GetHashCode() => HashCode.Combine(RequestId, Capability, Request, ExpiresAt, TrustedCredentialName);
}
