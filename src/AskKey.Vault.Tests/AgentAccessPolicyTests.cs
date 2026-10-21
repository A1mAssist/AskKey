using AskKey.Vault;

namespace AskKey.Vault.Tests;

/// <summary>
/// The pause flag and the security settings must not be reachable by editing a
/// user-writable file or database row. These cover the reported attacks: deleting the
/// pause row, and hand-editing settings.json to lengthen a session or drop read
/// authentication.
/// </summary>
public sealed class AgentAccessPolicyTests
{
    private const string PolicyKey = "agent_access_policy";

    private static VaultStore NewStore(TestDirectory root, byte[] key)
        => TestCredentials.NewStore(root.Combine("library.db"), key);

    [Fact]
    public void ABrandNewLibraryGetsDefaultsAndPersistsAVerifiableRecord()
    {
        using var root = new TestDirectory();
        var key = TestCredentials.Key(11);
        using var store = NewStore(root, key);
        var written = new List<string?>();

        var policy = AgentAccessPolicy.Load(
            store.ConfigValue,
            (name, value) => { written.Add(value); store.SetConfigValue(name, value); },
            key,
            isNewLibrary: true);

        Assert.False(policy.AgentAccessPaused);
        Assert.True(policy.ReadApprovalAuthenticationEnabled);
        Assert.Single(written);
        Assert.NotNull(store.ConfigValue(PolicyKey));
    }

    [Fact]
    public void AnExistingLibraryWithNoPolicyLoadsPaused()
    {
        using var root = new TestDirectory();
        var key = TestCredentials.Key(12);
        using var store = NewStore(root, key);

        // Simulate a library created before this policy existed: it has rows but no record.
        var policy = AgentAccessPolicy.Load(store.ConfigValue, store.SetConfigValue, key, isNewLibrary: false);

        Assert.True(policy.AgentAccessPaused);
    }

    [Fact]
    public void ADeletedPolicyRecordReadsAsPausedRatherThanUnpaused()
    {
        using var root = new TestDirectory();
        var key = TestCredentials.Key(13);
        using var store = NewStore(root, key);
        AgentAccessPolicy.TryWrite(new AgentAccessPolicy { AgentAccessPaused = false }, store.SetConfigValue, key);
        Assert.NotNull(store.ConfigValue(PolicyKey));

        // A same-user process removes the row while Ask Key is stopped.
        store.SetConfigValue(PolicyKey, null);

        var policy = AgentAccessPolicy.Load(store.ConfigValue, store.SetConfigValue, key, isNewLibrary: false);
        Assert.True(policy.AgentAccessPaused);
    }

    [Fact]
    public void AnEditedPolicyRecordFailsClosed()
    {
        using var root = new TestDirectory();
        var key = TestCredentials.Key(14);
        using var store = NewStore(root, key);
        AgentAccessPolicy.TryWrite(new AgentAccessPolicy { AgentAccessPaused = true }, store.SetConfigValue, key);

        // Flip the ciphertext to say "not paused" without the key.
        var raw = Convert.FromBase64String(store.ConfigValue(PolicyKey)!);
        raw[^1] ^= 0xFF;
        store.SetConfigValue(PolicyKey, Convert.ToBase64String(raw));

        var policy = AgentAccessPolicy.Load(store.ConfigValue, store.SetConfigValue, key, isNewLibrary: false);
        Assert.True(policy.AgentAccessPaused);
    }

    [Fact]
    public void APolicyFromAnotherVaultFailsClosed()
    {
        using var root = new TestDirectory();
        var key = TestCredentials.Key(15);
        var otherKey = TestCredentials.Key(99);
        using var store = NewStore(root, key);
        AgentAccessPolicy.TryWrite(new AgentAccessPolicy { AgentAccessPaused = false }, store.SetConfigValue, otherKey);

        var policy = AgentAccessPolicy.Load(store.ConfigValue, store.SetConfigValue, key, isNewLibrary: false);
        Assert.True(policy.AgentAccessPaused);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(61)]
    [InlineData(int.MaxValue)]
    public void AnOutOfRangeTimedAllowanceFallsBackToTheStrictDefault(int minutes)
    {
        using var root = new TestDirectory();
        var key = TestCredentials.Key(16);
        using var store = NewStore(root, key);
        AgentAccessPolicy.TryWrite(
            new AgentAccessPolicy { AgentAccessPaused = false, DefaultTimedAllowanceMinutes = minutes },
            store.SetConfigValue, key);

        var policy = AgentAccessPolicy.Load(store.ConfigValue, store.SetConfigValue, key, isNewLibrary: false);
        Assert.Equal(5, policy.DefaultTimedAllowanceMinutes);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(3601)]
    [InlineData(int.MaxValue)]
    public void AnOutOfRangeSessionWindowFallsBackToTheStrictDefault(int seconds)
    {
        using var root = new TestDirectory();
        var key = TestCredentials.Key(17);
        using var store = NewStore(root, key);
        AgentAccessPolicy.TryWrite(
            new AgentAccessPolicy { AgentAccessPaused = false, SessionTimeoutSeconds = seconds },
            store.SetConfigValue, key);

        var policy = AgentAccessPolicy.Load(store.ConfigValue, store.SetConfigValue, key, isNewLibrary: false);
        Assert.Equal(300, policy.SessionTimeoutSeconds);
    }

    [Fact]
    public void AValidPolicyRoundTripsAndKeepsItsValues()
    {
        using var root = new TestDirectory();
        var key = TestCredentials.Key(18);
        using var store = NewStore(root, key);
        var saved = new AgentAccessPolicy
        {
            AgentAccessPaused = true,
            ReadApprovalAuthenticationEnabled = false,
            TimedAllowanceEnabled = false,
            DefaultTimedAllowanceMinutes = 15,
            SessionTimeoutSeconds = 900,
        };
        Assert.True(AgentAccessPolicy.TryWrite(saved, store.SetConfigValue, key));

        var loaded = AgentAccessPolicy.Load(store.ConfigValue, store.SetConfigValue, key, isNewLibrary: false);
        Assert.True(loaded.AgentAccessPaused);
        Assert.False(loaded.ReadApprovalAuthenticationEnabled);
        Assert.False(loaded.TimedAllowanceEnabled);
        Assert.Equal(15, loaded.DefaultTimedAllowanceMinutes);
        Assert.Equal(900, loaded.SessionTimeoutSeconds);
    }

    [Fact]
    public void ThePolicyIsEncryptedRatherThanStoredAsPlainJson()
    {
        using var root = new TestDirectory();
        var key = TestCredentials.Key(19);
        using var store = NewStore(root, key);
        AgentAccessPolicy.TryWrite(
            new AgentAccessPolicy { AgentAccessPaused = false, SessionTimeoutSeconds = 3600 },
            store.SetConfigValue, key);

        var stored = store.ConfigValue(PolicyKey)!;
        Assert.DoesNotContain("3600", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentAccessPaused", stored, StringComparison.Ordinal);
    }
}
