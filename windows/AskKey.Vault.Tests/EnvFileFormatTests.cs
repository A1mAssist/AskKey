using AskKey.Vault;

namespace AskKey.Vault.Tests;

public sealed class EnvFileFormatTests
{
    [Fact]
    public void ParsesCommentsExportBareAndQuotedValues()
    {
        var parsed = EnvFileFormat.ParseValidated("""
            # comment
            PLAIN=value
            export EXPORTED=exported-value
            SINGLE='single value'
            DOUBLE="line\nnext"
            EMPTY=
            TRAILING=value # comment
            """);

        Assert.Equal(new[]
        {
            ("PLAIN", "value"),
            ("EXPORTED", "exported-value"),
            ("SINGLE", "single value"),
            ("DOUBLE", "line\nnext"),
            ("EMPTY", ""),
            ("TRAILING", "value"),
        }, parsed);
    }

    [Fact]
    public void ParsesDoubleQuotedEscapesAndHashInsideQuotes()
    {
        var parsed = EnvFileFormat.ParseValidated("""
            ESCAPED="a\\b\"c\nd\re\tf"
            HASH="not # a comment"
            SINGLE='backslash \ stays'
            """);

        Assert.Equal("a\\b\"c\nd\re\tf", parsed[0].Value);
        Assert.Equal("not # a comment", parsed[1].Value);
        Assert.Equal("backslash \\ stays", parsed[2].Value);
    }

    [Fact]
    public void LegacyParserIgnoresKeylessLinesAndReturnsNoPartialResultOnErrors()
    {
        Assert.Equal(new[] { ("A", "1") }, EnvFileFormat.Parse("keyless\nA=1"));
        Assert.Empty(EnvFileFormat.Parse("A=1\nB=\"unterminated"));
    }

    [Fact]
    public void RejectsMissingEquals()
    {
        var exception = Assert.Throws<EnvFileFormatException>(() => EnvFileFormat.ParseValidated("KEY"));
        Assert.Equal(EnvFileFormatErrorKind.InvalidLine, exception.Kind);
        Assert.Equal(1, exception.Line);
    }

    [Fact]
    public void RejectsInvalidKeys()
    {
        foreach (var line in new[] { "=value", "1KEY=value", "KEY-NAME=value" })
        {
            var exception = Assert.Throws<EnvFileFormatException>(() => EnvFileFormat.ParseValidated(line));
            Assert.Equal(EnvFileFormatErrorKind.InvalidKey, exception.Kind);
            Assert.Equal(1, exception.Line);
        }
    }

    [Fact]
    public void RejectsDuplicateKeys()
    {
        var exception = Assert.Throws<EnvFileFormatException>(() => EnvFileFormat.ParseValidated("A=1\nA=2"));
        Assert.Equal(EnvFileFormatErrorKind.DuplicateKey, exception.Kind);
        Assert.Equal(2, exception.Line);
    }

    [Fact]
    public void RejectsUnterminatedQuoteAndMultilineValue()
    {
        var single = Assert.Throws<EnvFileFormatException>(() => EnvFileFormat.ParseValidated("A=\"value"));
        Assert.Equal(EnvFileFormatErrorKind.UnterminatedQuote, single.Kind);

        var multiline = Assert.Throws<EnvFileFormatException>(() => EnvFileFormat.ParseValidated("A=\"value\nB=2"));
        Assert.Equal(EnvFileFormatErrorKind.MultilineValue, multiline.Kind);
        Assert.Equal(1, multiline.Line);
    }

    [Fact]
    public void RejectsInvalidEscapeAndTrailingCharacters()
    {
        var escape = Assert.Throws<EnvFileFormatException>(() => EnvFileFormat.ParseValidated("A=\"bad\\q\""));
        Assert.Equal(EnvFileFormatErrorKind.InvalidEscape, escape.Kind);

        var trailing = Assert.Throws<EnvFileFormatException>(() => EnvFileFormat.ParseValidated("A=\"value\"tail"));
        Assert.Equal(EnvFileFormatErrorKind.TrailingCharacters, trailing.Kind);
    }

    [Fact]
    public void LineEscapesValuesAndRoundTripsThroughValidatedParser()
    {
        var value = "back\\slash \"quote\" line\nnext\ttab\rreturn";
        var line = EnvFileFormat.Line("KEY", value);
        Assert.Equal("KEY=\"back\\\\slash \\\"quote\\\" line\\nnext\\ttab\\rreturn\"", line);
        var parsed = EnvFileFormat.ParseValidated(line);
        Assert.Equal(("KEY", value), parsed.Single());
    }
}
