namespace AskKey.Broker.Approval;

public sealed partial class BrokerApprovalStateMachine
{
    public void SetReadAuthenticationEnabled(bool enabled)
    {
        Mutate(() => _readAuthenticationEnabled = enabled);
    }

    public void ConfigureAuthentication(Func<BrokerAuthenticationPurpose, bool> authenticate)
    {
        lock (_observerLock)
        {
            _authenticate = authenticate;
        }
    }
}
