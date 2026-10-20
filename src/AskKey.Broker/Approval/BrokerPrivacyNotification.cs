namespace AskKey.Broker.Approval;

/// <summary>Deliberately carries no request, credential, caller or payload detail.</summary>
public sealed class BrokerPrivacyNotification : IEquatable<BrokerPrivacyNotification>
{
    public string Title { get; }
    public string Body { get; }
    public IReadOnlyList<string> Actions { get; }

    public BrokerPrivacyNotification(string title, string body, IReadOnlyList<string> actions)
    {
        Title = title;
        Body = body;
        Actions = actions;
    }

    public static BrokerPrivacyNotification ApprovalQueueBecameNonempty { get; } = new(
        "Ask Key needs your attention",
        "Open Ask Key to review pending requests.",
        Array.Empty<string>());

    public bool Equals(BrokerPrivacyNotification? other)
        => other is not null
            && Title == other.Title
            && Body == other.Body
            && Actions.SequenceEqual(other.Actions, StringComparer.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as BrokerPrivacyNotification);

    public override int GetHashCode() => HashCode.Combine(Title, Body, Actions.Count);
}
