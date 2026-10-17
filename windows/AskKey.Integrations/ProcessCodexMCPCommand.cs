namespace AskKey.Integrations;

/// <summary>
/// Builds the optional official-CLI bridge for Codex. The CLI is used only as an
/// isolated preflight against a throwaway CODEX_HOME; the lossless writer stays
/// authoritative for the real user config.
/// </summary>
public static class ProcessCodexMCPCommand
{
    public static CodexMCPCommand Make(string executable)
    {
        return new CodexMCPCommand(
            status: () =>
            {
                if (!HelperTrust.IsExecutableRegularFile(executable)) return CodexMCPCLIStatus.Missing;
                string versionOutput;
                try
                {
                    versionOutput = IntegrationProcess.RunStrict(
                        executable, new[] { "--version" }, IntegrationProcess.PortableEnvironment());
                }
                catch (CodexUserMCPException)
                {
                    return CodexMCPCLIStatus.Unknown(null);
                }
                var version = CodexVersion(versionOutput);
                if (version is null || !CodexUserMCP.AllowsOfficialCLI(version))
                {
                    return CodexMCPCLIStatus.Unknown(version ?? versionOutput.Trim());
                }
                try
                {
                    _ = IntegrationProcess.RunStrict(
                        executable, new[] { "mcp", "add", "--help" }, IntegrationProcess.PortableEnvironment());
                }
                catch (CodexUserMCPException)
                {
                    return CodexMCPCLIStatus.Unknown(version);
                }
                return CodexMCPCLIStatus.Supported(version);
            },
            addAskKey: (helper, config) =>
            {
                var codexHome = Path.GetDirectoryName(config)!;
                var environment = IntegrationProcess.CurrentEnvironment();
                environment["CODEX_HOME"] = codexHome;
                environment["HOME"] = Path.GetDirectoryName(codexHome)!;
                environment["USERPROFILE"] = Path.GetDirectoryName(codexHome)!;
                _ = IntegrationProcess.RunStrict(
                    executable,
                    new[] { "mcp", "add", CodexUserMCP.ServerName, "--", helper, "mcp" },
                    environment);
            });
    }

    /// <summary>Accepts a bare version or the `codex-cli 0.x.y` form. A suffix is
    /// kept whole so a prerelease can never be mistaken for a supported release.</summary>
    internal static string? CodexVersion(string text)
    {
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 1) return tokens[0];
        return tokens.Length == 2 && tokens[0] == "codex-cli" ? tokens[1] : null;
    }
}