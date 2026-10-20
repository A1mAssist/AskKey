using System.Diagnostics;
using System.Text;
using AskKey.Integrations;

namespace AskKey.Integrations.Tests;

/// <summary>
/// Ask Key can read back its own output, which is not proof the file is valid TOML.
/// These run the production writer over the shapes that used to produce a file a
/// real parser rejects, then hand the result to Python's tomllib.
/// </summary>
public sealed class TomlValidityTests
{
    private static void AssertValidToml(string name, string text)
    {
        var directory = Path.Combine(Path.GetTempPath(), "askkey-toml", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, name);
            File.WriteAllText(file, text, new UTF8Encoding(false));
            var start = new ProcessStartInfo
            {
                FileName = "python",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(
                "import sys,tomllib;tomllib.load(open(sys.argv[1],'rb'));print('OK')");
            start.ArgumentList.Add(file);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(20000);
            Assert.True(process.ExitCode == 0,
                $"{name} is not valid TOML: {error}\n---\n{text}");
            Assert.Contains("OK", output, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void GrokStdioOutputIsValidTomlForEveryStartingShape()
    {
        string[] inputs =
        {
            "",
            "[ui]\ntheme = \"dark\"\n",
            "[ui]\r\ntheme = \"dark\"\r\n",
            "# lead\n\n[keep]\r\nx = 1\r\n",
            "[mcp_servers.askkey]\ncommand = \"old\"\nargs = []\n",
            "[mcp_servers.askkey]\ncommand = \"old\"\n\n[mcp_servers.askkey.env]\nA = \"1\"\n\n[keep]\nx = 1\n",
            "[keep]\nx = 1\n\n[mcp_servers.other]\ncommand = \"z\"\n",
        };
        foreach (var input in inputs)
        {
            var output = GrokUserTOML.UpsertStdio(
                input, "askkey", @"C:\AskKey\Helpers\askkey.exe",
                new[] { "mcp" }, new Dictionary<string, string> { ["ASKKEY_BROKER_PIPE"] = @"C:\x.pipe" });
            AssertValidToml("grok.toml", output);
            var transport = GrokUserTOML.AskKeyTransport(output);
            Assert.True(transport.IsStdio(@"C:\AskKey\Helpers\askkey.exe", new[] { "mcp" }),
                $"Ask Key entry did not round-trip for input <{input}>");
        }
    }

    [Fact]
    public void CodexStdioOutputIsValidTomlForEveryStartingShape()
    {
        string[] inputs =
        {
            "",
            "model = \"gpt-5\"\n",
            "model = \"gpt-5\"\r\n",
            "[mcp_servers.askkey]\ncommand = \"old\"\nargs = []\n",
            "[mcp_servers.askkey]\ncommand = \"old\"\n\n[mcp_servers.askkey.env]\nA = \"1\"\n\n[keep]\nx = 1\n",
            "[keep]\nx = 1\n\n[mcp_servers.other]\ncommand = \"z\"\n",
        };
        foreach (var input in inputs)
        {
            var output = CodexAskKeyTOML.Upsert(input, @"C:\AskKey\Helpers\askkey.exe", new[] { "mcp" });
            AssertValidToml("codex.toml", output);
        }
    }
}
