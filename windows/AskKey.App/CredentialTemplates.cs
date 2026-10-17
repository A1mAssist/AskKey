namespace AskKeyApp;

public enum CredentialTemplate
{
    Api,
    Ssh,
    GitHubApp,
    Apple,
    Cloud,
    Database,
    Custom,
}

public enum CredentialTemplateComponentKind
{
    Text,
    File,
}

internal sealed record CredentialTemplateComponent(
    string Name,
    CredentialTemplateComponentKind Kind,
    bool IsOptional = false);

internal static class CredentialTemplates
{
    public static IReadOnlyList<CredentialTemplate> Presets { get; } =
    [
        CredentialTemplate.Api,
        CredentialTemplate.Ssh,
        CredentialTemplate.GitHubApp,
        CredentialTemplate.Apple,
        CredentialTemplate.Cloud,
        CredentialTemplate.Database,
    ];

    public static IReadOnlyList<CredentialTemplate> Chooser { get; } =
        [.. Presets, CredentialTemplate.Custom];

    public static string PrototypeTitle(CredentialTemplate template) => template switch
    {
        CredentialTemplate.Api => "API Access Credential",
        CredentialTemplate.Ssh => "SSH Identity",
        CredentialTemplate.GitHubApp => "GitHub App",
        CredentialTemplate.Apple => "Apple Signing",
        CredentialTemplate.Cloud => "Cloud Account",
        CredentialTemplate.Database => "Database or Service Connection",
        _ => "Custom Credential",
    };

    public static string Description(CredentialTemplate template) => template switch
    {
        CredentialTemplate.Api => "Access key and optional API endpoint",
        CredentialTemplate.Ssh => "Connect to servers or deploy code",
        CredentialTemplate.GitHubApp => "App ID, client secret, and private key file",
        CredentialTemplate.Apple => "For App Store Connect and automated signing",
        CredentialTemplate.Cloud => "Access cloud platforms, storage, or CLIs",
        CredentialTemplate.Database => "Server, account, and password as one set",
        _ => "Add text or file values with keys, like .env",
    };

    public static string EditorTitle(CredentialTemplate template) => template switch
    {
        CredentialTemplate.Api => "New API access credential",
        CredentialTemplate.Ssh => "New SSH identity",
        CredentialTemplate.GitHubApp => "New GitHub App",
        CredentialTemplate.Apple => "New Apple signing credential",
        CredentialTemplate.Cloud => "New cloud account",
        CredentialTemplate.Database => "New database or service connection",
        _ => "New custom credential",
    };

    public static string Glyph(CredentialTemplate template) => template switch
    {
        CredentialTemplate.Api => "\uE8D7",
        CredentialTemplate.Ssh => "\uE756",
        CredentialTemplate.GitHubApp => "\uE943",
        CredentialTemplate.Apple => "\uE8A5",
        CredentialTemplate.Cloud => "\uE753",
        CredentialTemplate.Database => "\uE8F1",
        _ => "\uE710",
    };

    public static IReadOnlyList<CredentialTemplateComponent> Components(CredentialTemplate template) => template switch
    {
        CredentialTemplate.Api =>
        [
            Text("API_KEY"),
            Text("API_ENDPOINT", isOptional: true),
        ],
        CredentialTemplate.Ssh =>
        [
            Text("SSH_HOST"),
            Text("SSH_USER"),
            File("SSH_PRIVATE_KEY"),
            Text("SSH_PASSPHRASE", isOptional: true),
        ],
        CredentialTemplate.GitHubApp =>
        [
            Text("GITHUB_APP_ID"),
            Text("GITHUB_CLIENT_ID"),
            Text("GITHUB_CLIENT_SECRET"),
            File("GITHUB_PRIVATE_KEY"),
            Text("GITHUB_INSTALLATION_ID", isOptional: true),
        ],
        CredentialTemplate.Apple =>
        [
            Text("ISSUER_ID"),
            Text("KEY_ID"),
            Text("TEAM_ID"),
            File("PRIVATE_KEY_FILE"),
        ],
        CredentialTemplate.Cloud =>
        [
            Text("ACCESS_KEY_ID"),
            Text("SECRET_ACCESS_KEY"),
            Text("SESSION_TOKEN", isOptional: true),
            Text("REGION", isOptional: true),
        ],
        CredentialTemplate.Database =>
        [
            Text("DB_HOST"),
            Text("DB_PORT"),
            Text("DB_USER"),
            Text("DB_PASSWORD"),
            File("DB_CERT", isOptional: true),
        ],
        _ => [],
    };

    private static CredentialTemplateComponent Text(string name, bool isOptional = false)
        => new(name, CredentialTemplateComponentKind.Text, isOptional);

    private static CredentialTemplateComponent File(string name, bool isOptional = false)
        => new(name, CredentialTemplateComponentKind.File, isOptional);
}
