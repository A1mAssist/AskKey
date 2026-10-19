using AskKey.Vault;

namespace AskKeyApp.Tests;

public sealed class CredentialTemplateTests
{
    [Fact]
    public void ChooserListsSixPresetsThenCustom()
    {
        Assert.Equal(
            new[]
            {
                CredentialTemplate.Api,
                CredentialTemplate.Ssh,
                CredentialTemplate.GitHubApp,
                CredentialTemplate.Apple,
                CredentialTemplate.Cloud,
                CredentialTemplate.Database,
                CredentialTemplate.Custom,
            },
            CredentialTemplates.Chooser.ToArray());
        Assert.Equal(6, CredentialTemplates.Presets.Count);
    }

    [Fact]
    public void EveryTemplateHasPresentationCopy()
    {
        foreach (var template in CredentialTemplates.Chooser)
        {
            Assert.False(string.IsNullOrWhiteSpace(CredentialTemplates.PrototypeTitle(template)));
            Assert.False(string.IsNullOrWhiteSpace(CredentialTemplates.Description(template)));
            Assert.False(string.IsNullOrWhiteSpace(CredentialTemplates.EditorTitle(template)));
            Assert.False(string.IsNullOrWhiteSpace(CredentialTemplates.Glyph(template)));
        }
    }

    public static TheoryData<string, string, string, string> PresetMetadata => new()
    {
        { "Api", "API Access Credential", "Access key and optional API endpoint", "New API access credential" },
        { "Ssh", "SSH Identity", "Connect to servers or deploy code", "New SSH identity" },
        { "GitHubApp", "GitHub App", "App ID, client secret, and private key file", "New GitHub App" },
        { "Apple", "Apple Signing", "For App Store Connect and automated signing", "New Apple signing credential" },
        { "Cloud", "Cloud Account", "Access cloud platforms, storage, or CLIs", "New cloud account" },
        { "Database", "Database or Service Connection", "Server, account, and password as one set", "New database or service connection" },
    };

    [Theory]
    [MemberData(nameof(PresetMetadata))]
    public void PresetMetadataMatchesMacTemplates(
        string templateName,
        string expectedTitle,
        string expectedDescription,
        string expectedEditorTitle)
    {
        var template = Enum.Parse<CredentialTemplate>(templateName);

        Assert.Equal(expectedTitle, CredentialTemplates.PrototypeTitle(template));
        Assert.Equal(expectedDescription, CredentialTemplates.Description(template));
        Assert.Equal(expectedEditorTitle, CredentialTemplates.EditorTitle(template));
    }

    [Fact]
    public void PresetsUseBundlesAndCustomIsEmpty()
    {
        foreach (var template in CredentialTemplates.Presets)
        {
            var components = CredentialTemplates.Components(template);
            Assert.True(components.Count > 1);
            Assert.Equal(components.Count, components.Select(component => component.Name).Distinct().Count());
        }

        Assert.Empty(CredentialTemplates.Components(CredentialTemplate.Custom));
    }

    [Fact]
    public void OptionalTemplateComponentsMatchMacTemplates()
    {
        Assert.Equal(["API_ENDPOINT"], OptionalNames(CredentialTemplate.Api));
        Assert.Equal(["SSH_PASSPHRASE"], OptionalNames(CredentialTemplate.Ssh));
        Assert.Equal(["GITHUB_INSTALLATION_ID"], OptionalNames(CredentialTemplate.GitHubApp));
        Assert.Empty(OptionalNames(CredentialTemplate.Apple));
        Assert.Equal(["SESSION_TOKEN", "REGION"], OptionalNames(CredentialTemplate.Cloud));
        Assert.Equal(["DB_CERT"], OptionalNames(CredentialTemplate.Database));
    }

    [Fact]
    public void BlankOptionalComponentsAreOmittedFromTheBundle()
    {
        var optionalText = new CredentialEditorWindow.ComponentDraft("API_ENDPOINT", isOptional: true);
        var optionalFile = new CredentialEditorWindow.ComponentDraft("DB_CERT", isFile: true, isOptional: true);

        Assert.Null(optionalText.ToInput());
        Assert.Null(optionalFile.ToInput());
    }

    [Fact]
    public void RequiredFileComponentsStillRequireAFile()
    {
        var requiredFile = new CredentialEditorWindow.ComponentDraft("SSH_PRIVATE_KEY", isFile: true);

        Assert.Throws<InvalidOperationException>(() => requiredFile.ToInput());
    }

    [Fact]
    public void ProvidedOptionalComponentsRemainInTheBundle()
    {
        var optionalText = new CredentialEditorWindow.ComponentDraft("REGION", isOptional: true)
        {
            Value = "us-east-1",
        };
        var optionalFile = new CredentialEditorWindow.ComponentDraft("DB_CERT", isFile: true, isOptional: true);
        optionalFile.SetFile("certificate.pem", [1, 2, 3]);

        var textInput = Assert.IsType<CredentialComponentInput>(optionalText.ToInput());
        var fileInput = Assert.IsType<CredentialComponentInput>(optionalFile.ToInput());
        Assert.Equal("us-east-1", Assert.IsType<CredentialComponentValue.Text>(textInput.Value).Value);
        Assert.IsType<CredentialComponentValue.File>(fileInput.Value);
    }

    private static string[] OptionalNames(CredentialTemplate template)
        => CredentialTemplates.Components(template)
            .Where(component => component.IsOptional)
            .Select(component => component.Name)
            .ToArray();

    public static TheoryData<string, string> PresetShapes => new()
    {
        { "Api", "API_KEY:text,API_ENDPOINT:text" },
        { "Ssh", "SSH_HOST:text,SSH_USER:text,SSH_PRIVATE_KEY:file,SSH_PASSPHRASE:text" },
        { "GitHubApp", "GITHUB_APP_ID:text,GITHUB_CLIENT_ID:text,GITHUB_CLIENT_SECRET:text,GITHUB_PRIVATE_KEY:file,GITHUB_INSTALLATION_ID:text" },
        { "Apple", "ISSUER_ID:text,KEY_ID:text,TEAM_ID:text,PRIVATE_KEY_FILE:file" },
        { "Cloud", "ACCESS_KEY_ID:text,SECRET_ACCESS_KEY:text,SESSION_TOKEN:text,REGION:text" },
        { "Database", "DB_HOST:text,DB_PORT:text,DB_USER:text,DB_PASSWORD:text,DB_CERT:file" },
    };

    [Theory]
    [MemberData(nameof(PresetShapes))]
    public void PresetComponentShapesMatchMacTemplates(string templateName, string expected)
    {
        var template = Enum.Parse<CredentialTemplate>(templateName);
        var actual = string.Join(",", CredentialTemplates.Components(template).Select(component =>
            $"{component.Name}:{(component.Kind == CredentialTemplateComponentKind.File ? "file" : "text")}"));

        Assert.Equal(expected, actual);
    }
}
