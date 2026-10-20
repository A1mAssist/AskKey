using AskKey.Integrations;

namespace AskKey.Integrations.Tests;

public sealed class GrokSpliceTests
{
    private static string Upsert(string text)
        => GrokUserTOML.UpsertStdio(
            text, "askkey", @"C:\Program Files\AskKey\current\Helpers\askkey.exe",
            new[] { "mcp" }, new Dictionary<string, string> { ["ASKKEY_BROKER_PIPE"] = @"C:\x.pipe" });

    [Fact]
    public void AnExistingAskKeyTableIsReplacedInPlace()
    {
        var input = "[ui]\r\ntheme = \"dark\"\r\n\r\n[mcp_servers.askkey]\r\ncommand = \"old\"\r\n\r\n[keep]\r\nvalue = 1\r\n";
        var output = Upsert(input);

        Assert.Contains("[keep]", output, StringComparison.Ordinal);
        Assert.Contains("[ui]", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\"old\"", output, StringComparison.Ordinal);
        Assert.Equal(1, Count(output, "[mcp_servers.askkey]"));
        // Everything outside the owned block is byte-identical.
        Assert.Contains("theme = \"dark\"\r\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ANewlineStyleIsPreserved()
    {
        var crlf = Upsert("[ui]\r\ntheme = \"dark\"\r\n");
        var lf = Upsert("[ui]\ntheme = \"dark\"\n");
        Assert.Contains("\r\n", crlf, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n\r", crlf, StringComparison.Ordinal);
        Assert.Contains("\n", lf, StringComparison.Ordinal);
        Assert.DoesNotContain("\r\n", lf, StringComparison.Ordinal);
    }

    [Fact]
    public void BlankLinesAndCommentsSurvive()
    {
        var input = "# my config\n[ui]\n\n\n    # keep me\ntheme = \"dark\"\n\n\n[keep]\nvalue = 1\n";
        var output = Upsert(input);
        Assert.Contains("# my config", output, StringComparison.Ordinal);
        Assert.Contains("# keep me", output, StringComparison.Ordinal);
        Assert.Contains("[keep]", output, StringComparison.Ordinal);
        // The three blank lines between [ui] and its body are still there.
        Assert.Contains("[ui]\n\n\n    # keep me", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AServerDeclaredAsADottedKeyIsRefused()
    {
        var exception = Assert.Throws<GrokCLIAdapterException>(
            () => Upsert("mcp_servers.askkey = { command = \"old\" }\n"));
        Assert.Equal(GrokCLIAdapterError.InvalidConfig, exception.Error);
    }

    [Fact]
    public void AServerInsideAnInlineTableIsRefused()
    {
        var exception = Assert.Throws<GrokCLIAdapterException>(
            () => Upsert("mcp_servers = { askkey = { command = \"old\" }, other = 1 }\n"));
        Assert.Equal(GrokCLIAdapterError.InvalidConfig, exception.Error);
    }

    [Fact]
    public void AnInlineTableWithoutOurServerIsLeftAlone()
    {
        var output = Upsert("mcp_servers = { other = { command = \"x\" } }\n");
        Assert.Contains("other = { command = \"x\" }", output, StringComparison.Ordinal);
        Assert.Contains("[mcp_servers.askkey]", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ChildTablesUnderTheServerAreAlsoReplaced()
    {
        var output = Upsert("[mcp_servers.askkey]\ncommand = \"old\"\n\n[mcp_servers.askkey.env]\nA = \"1\"\n\n[keep]\nx = 1\n");
        Assert.Equal(1, Count(output, "[mcp_servers.askkey]"));
        Assert.DoesNotContain("[mcp_servers.askkey.env]", output, StringComparison.Ordinal);
        Assert.Contains("[keep]", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyFileGetsOnlyTheServerTable()
    {
        var output = Upsert("");
        Assert.StartsWith("[mcp_servers.askkey]", output, StringComparison.Ordinal);
        Assert.Contains("enabled = true", output, StringComparison.Ordinal);
    }

    private static int Count(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
