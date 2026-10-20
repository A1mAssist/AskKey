using AskKey.Integrations;

namespace AskKey.Integrations.Tests;

/// <summary>
/// The writer only edits shapes it can edit without producing a file a real TOML
/// parser rejects. Anything else is refused instead of duplicated.
/// </summary>
public sealed class CodexUneditableShapeTests
{
    private static string Upsert(string text)
        => CodexAskKeyTOML.Upsert(text, @"C:\AskKey\Helpers\askkey.exe", new[] { "mcp" });

    [Fact]
    public void ADottedServerAssignmentIsReplaced()
    {
        var output = Upsert("mcp_servers.askkey = { command = \"old\" }\n");
        Assert.DoesNotContain("\"old\"", output, StringComparison.Ordinal);
        Assert.Equal(1, CountHeader(output));
    }

    [Fact]
    public void AnInlineTableHoldingOnlyOurServerIsReplaced()
    {
        var output = Upsert("mcp_servers = { askkey = { command = \"old\" } }\n");
        Assert.DoesNotContain("mcp_servers = ", output, StringComparison.Ordinal);
        Assert.Equal(1, CountHeader(output));
    }

    [Fact]
    public void AnInlineTableMixingOurServerWithOthersIsRefused()
    {
        var error = Assert.Throws<CodexUserMCPException>(
            () => Upsert("mcp_servers = { askkey = { command = \"old\" }, other = { command = \"z\" } }\n"));
        Assert.Equal(CodexUserMCPError.IllegalConfig, error.Error);
    }

    [Fact]
    public void AnInlineTableWithoutOurServerIsLeftAlone()
    {
        var output = Upsert("mcp_servers = { other = { command = \"z\" } }\n");
        Assert.Contains("other = { command = \"z\" }", output, StringComparison.Ordinal);
        Assert.Equal(1, CountHeader(output));
    }

    [Fact]
    public void MixedLineEndingsAreRefusedRatherThanGuessed()
    {
        var error = Assert.Throws<CodexUserMCPException>(
            () => Upsert("model = \"gpt-5\"\r\n[mcp_servers.askkey]\ncommand = \"old\"\n"));
        Assert.Equal(CodexUserMCPError.IllegalConfig, error.Error);
    }

    [Fact]
    public void ChildTablesUnderTheServerAreReplacedToo()
    {
        var output = Upsert(
            "[mcp_servers.askkey]\ncommand = \"old\"\n\n[mcp_servers.askkey.env]\nA = \"1\"\n\n[keep]\nx = 1\n");
        Assert.Equal(1, CountHeader(output));
        Assert.DoesNotContain("[mcp_servers.askkey.env]", output, StringComparison.Ordinal);
        Assert.Contains("[keep]", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFileKeepsItsOwnLineEndings()
    {
        Assert.Contains("\r\n", Upsert("model = \"gpt-5\"\r\n"), StringComparison.Ordinal);
        var lf = Upsert("model = \"gpt-5\"\n");
        Assert.DoesNotContain("\r\n", lf, StringComparison.Ordinal);
    }

    private static int CountHeader(string text)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf("[mcp_servers.askkey]", index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += 1;
        }
        return count;
    }
}
