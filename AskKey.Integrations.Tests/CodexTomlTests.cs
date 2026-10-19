namespace AskKey.Integrations.Tests;

public class CodexTomlTests
{
    [Fact]
    public void UpsertPreservesEveryOtherByte()
    {
        var original = "# header\nmodel = \"o3\"\n\n[other]\nvalue = 1 # keep\n";

        var updated = CodexAskKeyTOML.Upsert(original, @"C:\Program Files\AskKey\askkey.exe", new[] { "mcp" });

        Assert.StartsWith("# header\nmodel = \"o3\"\n\n[other]\nvalue = 1 # keep\n", updated, StringComparison.Ordinal);
        Assert.Contains("[mcp_servers.askkey]", updated, StringComparison.Ordinal);
        Assert.Contains(@"command = ""C:\\Program Files\\AskKey\\askkey.exe""", updated, StringComparison.Ordinal);
        Assert.True(CodexAskKeyTOML.HasServer(updated, "askkey"));
        Assert.True(CodexAskKeyTOML.PreservesNonAskKey(original, updated));
    }

    [Fact]
    public void UpsertIsIdempotentAndReplacesOnlyAskKeyTable()
    {
        const string original = "theme = \"dark\"\n\n[mcp_servers.askkey]\ncommand = \"old.exe\"\nargs = [\"mcp\"]\n\n[mcp_servers.other]\ncommand = \"other.exe\"\n";
        var once = CodexAskKeyTOML.Upsert(original, @"C:\new\askkey.exe", new[] { "mcp" });
        var twice = CodexAskKeyTOML.Upsert(once, @"C:\new\askkey.exe", new[] { "mcp" });

        Assert.Equal(once, twice, StringComparer.Ordinal);
        Assert.Contains("[mcp_servers.other]", twice, StringComparison.Ordinal);
        Assert.DoesNotContain("old.exe", twice, StringComparison.Ordinal);
        var entry = CodexAskKeyTOML.AskKey(twice);
        Assert.NotNull(entry);
        Assert.Equal(@"C:\new\askkey.exe", entry!.Value.Command);
        Assert.Equal(new[] { "mcp" }, entry.Value.Args);
    }

    [Fact]
    public void CrlfInputKeepsItsLineEndingsOutsideTheAskKeyTable()
    {
        const string original = "model = \"o3\"\r\n\r\n[other]\r\nvalue = 1\r\n";
        var updated = CodexAskKeyTOML.Upsert(original, "askkey.exe", new[] { "mcp" });

        Assert.Contains("model = \"o3\"\r\n", updated, StringComparison.Ordinal);
        Assert.Contains("[other]\r\nvalue = 1\r\n", updated, StringComparison.Ordinal);
        Assert.True(CodexAskKeyTOML.HasServer(updated, "askkey"));
    }

    [Theory]
    [InlineData("[mcp_servers.askkey]\ncommand = \"unterminated\n")]
    [InlineData("[mcp_servers.askkey]\nargs = [\"a\", \"b\"\n")]
    [InlineData("[mcp_servers.askkey]\ncommand = \"a\"\n[mcp_servers.askkey]\ncommand = \"b\"\n")]
    [InlineData("[mcp_servers.askkey]\n= 1\n")]
    [InlineData("mcp_servers.askkey.command = \"a\"\nmcp_servers.askkey.command = \"b\"\n")]
    [InlineData("[mcp_servers.askkey]\ncommand = \"\"\"unterminated\n")]
    public void InvalidTomlFailsClosed(string text)
        => Assert.ThrowsAny<Exception>(() => CodexAskKeyTOML.Validate(text));

    [Fact]
    public void MultilineStringsAndInlineTablesAreAccepted()
    {
        const string text = """"
            [mcp_servers.askkey]
            command = "askkey.exe"
            args = [
              "mcp",
              "other",
            ]
            env = { TOKEN = "x", NESTED = { A = 1 } }
            notes = """
            line one
            line two
            """
            """";

        CodexAskKeyTOML.Validate(text);
        Assert.True(CodexAskKeyTOML.HasServer(text, "askkey"));
        // Swift's askKey() only reads a single-line args array; the multiline form
        // is accepted by validation but not extracted.
        Assert.NotNull(CodexAskKeyTOML.AskKey("""
            [mcp_servers.askkey]
            command = "askkey.exe"
            args = ["mcp", "other"]
            """));
    }

    [Fact]
    public void DottedKeyAndQuotedTableNamesAreUnderstood()
    {
        const string text = "mcp_servers.\"askkey\".command = \"askkey.exe\"\nmcp_servers.\"askkey\".args = [\"mcp\"]\n";

        CodexAskKeyTOML.Validate(text);
        Assert.True(CodexAskKeyTOML.HasServer(text, "askkey"));
    }

    [Fact]
    public void RedactionHidesSecretsAndLeavesStructure()
    {
        const string text = "api_key = \"secret\"\n[mcp_servers.askkey]\ncommand = \"askkey.exe\"\nenv = { TOKEN = \"abc\" }\n";

        var redacted = CodexTomlRedaction.Redact(text);

        Assert.DoesNotContain("abc", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", redacted, StringComparison.Ordinal);
        Assert.Contains("[mcp_servers.askkey]", redacted, StringComparison.Ordinal);
        // All quoted values are masked, matching redactCodexLine in Swift.
        Assert.Contains("command = \"***\"", redacted, StringComparison.Ordinal);
    }
    [Fact]
    public void DiffDescriptionNeverLeaksSecretValues()
    {
        var diff = new CodexConfigDiff("token = \"abc\"\n", "token = \"def\"\n");

        Assert.DoesNotContain("abc", diff.RedactedDescription, StringComparison.Ordinal);
        Assert.DoesNotContain("def", diff.RedactedDescription, StringComparison.Ordinal);
    }
}
