using AskKey.Vault;

namespace AskKey.Vault.Tests;

public sealed class CredentialExpiryReminderPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SchedulesSevenDaysBeforeExpiry()
    {
        var expiresAt = Now.AddDays(10);
        var work = CredentialExpiryReminderPolicy.Work(
            new CredentialExpirySnapshot("credential", expiresAt), null, Now);

        var schedule = Assert.IsType<ExpiryReminderWork.Schedule>(work);
        Assert.Equal(expiresAt.AddDays(-7), schedule.At);
    }

    [Fact]
    public void DeliversWhenExpiryIsInsideLeadTime()
    {
        var work = CredentialExpiryReminderPolicy.Work(
            new CredentialExpirySnapshot("credential", Now.AddDays(7)), null, Now);

        Assert.IsType<ExpiryReminderWork.Deliver>(work);
    }

    [Fact]
    public void CancelsExpiredCredentialOnlyAfterItWasRecorded()
    {
        var expired = new CredentialExpirySnapshot("credential", Now.AddMinutes(-1));
        var ledger = new ExpiryReminderLedgerEntry { ExpiresAt = Now.AddMinutes(-1), Delivered = true };

        Assert.Null(CredentialExpiryReminderPolicy.Work(expired, null, Now));
        Assert.IsType<ExpiryReminderWork.Cancel>(
            CredentialExpiryReminderPolicy.Work(expired, ledger, Now));
    }

    [Fact]
    public void DoesNothingWhenRecordedExpiryHasNotChanged()
    {
        var expiresAt = Now.AddDays(3);
        var ledger = new ExpiryReminderLedgerEntry { ExpiresAt = expiresAt, Delivered = true };

        Assert.Null(CredentialExpiryReminderPolicy.Work(
            new CredentialExpirySnapshot("credential", expiresAt), ledger, Now));
    }

    [Fact]
    public void ReplansWhenExpiryChanges()
    {
        var ledger = new ExpiryReminderLedgerEntry { ExpiresAt = Now.AddDays(3), Delivered = true };
        var expiresAt = Now.AddDays(10);
        var work = CredentialExpiryReminderPolicy.Work(
            new CredentialExpirySnapshot("credential", expiresAt), ledger, Now);

        var schedule = Assert.IsType<ExpiryReminderWork.Schedule>(work);
        Assert.Equal(expiresAt.AddDays(-7), schedule.At);
    }
}