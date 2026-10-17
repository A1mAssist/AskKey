using System.Globalization;
using System.Text;

namespace AskKey.Broker.Approval;

/// <summary>Authorization inputs bind retransmission and consumption; display never does.</summary>
public sealed class BrokerApprovalOperationRequest : IEquatable<BrokerApprovalOperationRequest>
{
    public string OperationId { get; }
    public string CredentialId { get; }
    public string TargetId { get; }
    public BrokerApprovalOperation Operation { get; }
    public string PayloadDigest { get; }
    public string? RetransmissionDigest { get; }
    public string? CredentialName { get; }
    public string? CallerName { get; }
    public string? CallerPurpose { get; }
    /// <summary>App-derived presentation only; never an authorization input.</summary>
    public Display? DisplayValue { get; }
    /// <summary>App-side batch binding. Never included in a Broker response.</summary>
    public IReadOnlyList<string>? OrganizationCredentialIds { get; }

    public BrokerApprovalOperationRequest(
        string operationId,
        string credentialId,
        string targetId,
        BrokerApprovalOperation operation,
        string payloadDigest,
        string? credentialName = null,
        string? callerName = null,
        string? callerPurpose = null,
        string? retransmissionDigest = null,
        Display? display = null,
        IReadOnlyList<string>? organizationCredentialIds = null)
    {
        OperationId = operationId;
        CredentialId = credentialId;
        TargetId = targetId;
        Operation = operation;
        PayloadDigest = payloadDigest;
        RetransmissionDigest = retransmissionDigest;
        CredentialName = credentialName;
        CallerName = callerName;
        CallerPurpose = callerPurpose;
        DisplayValue = display;
        OrganizationCredentialIds = organizationCredentialIds;
    }

    public bool Equals(BrokerApprovalOperationRequest? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return OperationId == other.OperationId
            && CredentialId == other.CredentialId
            && TargetId == other.TargetId
            && Operation == other.Operation
            && PayloadDigest == other.PayloadDigest
            && RetransmissionDigest == other.RetransmissionDigest
            && CredentialName == other.CredentialName
            && CallerName == other.CallerName
            && CallerPurpose == other.CallerPurpose
            && SameIds(OrganizationCredentialIds, other.OrganizationCredentialIds);
    }

    public override bool Equals(object? obj) => Equals(obj as BrokerApprovalOperationRequest);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(OperationId);
        hash.Add(CredentialId);
        hash.Add(TargetId);
        hash.Add(Operation);
        hash.Add(PayloadDigest);
        hash.Add(RetransmissionDigest);
        hash.Add(CredentialName);
        hash.Add(CallerName);
        hash.Add(CallerPurpose);
        if (OrganizationCredentialIds is { } ids)
        {
            foreach (var id in ids) hash.Add(id);
        }
        return hash.ToHashCode();
    }

    public static bool operator ==(BrokerApprovalOperationRequest? left, BrokerApprovalOperationRequest? right)
        => left is null ? right is null : left.Equals(right);

    public static bool operator !=(BrokerApprovalOperationRequest? left, BrokerApprovalOperationRequest? right)
        => !(left == right);

    internal bool MatchesForConsumption(BrokerApprovalOperationRequest other)
        => OperationId == other.OperationId
            && CredentialId == other.CredentialId
            && TargetId == other.TargetId
            && Operation == other.Operation
            && CredentialName == other.CredentialName
            && CallerName == other.CallerName
            && CallerPurpose == other.CallerPurpose
            && SameIds(OrganizationCredentialIds, other.OrganizationCredentialIds)
            && RetransmissionDigest == other.RetransmissionDigest
            && ConstantTimeEqual(PayloadDigest, other.PayloadDigest);

    internal static bool ConstantTimeEqual(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        if (leftBytes.Length != rightBytes.Length) return false;
        var difference = 0;
        for (var index = 0; index < leftBytes.Length; index++)
        {
            difference |= leftBytes[index] ^ rightBytes[index];
        }
        return difference == 0;
    }

    private static bool SameIds(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
        => left is null ? right is null : right is not null && left.SequenceEqual(right, StringComparer.Ordinal);

    public sealed class Display : IEquatable<Display>
    {
        public string CommandLine { get; }
        public string? WorkingDirectory { get; }
        public string? ExecutableBasename { get; }
        /// <summary>Null means the mapping is unavailable without decrypting values.</summary>
        public IReadOnlyList<string>? EnvironmentVariables { get; }
        public IReadOnlyList<string>? TemporaryFileVariables { get; }

        public Display(
            string commandLine,
            string? workingDirectory,
            string? executableBasename,
            IReadOnlyList<string>? environmentVariables,
            IReadOnlyList<string>? temporaryFileVariables)
        {
            CommandLine = commandLine;
            WorkingDirectory = workingDirectory;
            ExecutableBasename = executableBasename;
            EnvironmentVariables = environmentVariables;
            TemporaryFileVariables = temporaryFileVariables;
        }

        public string CommandSummary
        {
            get
            {
                var elements = GraphemeElements(CommandLine);
                if (elements.Count <= 160) return CommandLine;
                return string.Concat(elements.Take(80)) + "\u2026" + string.Concat(elements.Skip(elements.Count - 79));
            }
        }

        /// <summary>Swift counts Character (grapheme clusters); splitting one would render a broken cluster.</summary>
        private static List<string> GraphemeElements(string value)
        {
            var elements = new List<string>();
            var enumerator = StringInfo.GetTextElementEnumerator(value);
            while (enumerator.MoveNext()) elements.Add((string)enumerator.Current);
            return elements;
        }

        public bool Equals(Display? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return CommandLine == other.CommandLine
                && WorkingDirectory == other.WorkingDirectory
                && ExecutableBasename == other.ExecutableBasename
                && SameIds(EnvironmentVariables, other.EnvironmentVariables)
                && SameIds(TemporaryFileVariables, other.TemporaryFileVariables);
        }

        public override bool Equals(object? obj) => Equals(obj as Display);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(CommandLine);
            hash.Add(WorkingDirectory);
            hash.Add(ExecutableBasename);
            return hash.ToHashCode();
        }
    }
}