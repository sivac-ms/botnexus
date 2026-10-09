namespace BotNexus.Cli.Tests;

public sealed class CliPathsTests
{
    [Theory]
    [InlineData("explicit", "process", "user", "explicit")]
    [InlineData(null, "process", "user", "process")]
    [InlineData("", "process", "user", "process")]
    [InlineData(" ", "process", "user", "process")]
    [InlineData(null, null, "user", "user")]
    [InlineData(null, "", "user", "user")]
    [InlineData(null, " ", "user", "user")]
    public void ResolveSource_ConfiguredPaths_RespectsPrecedence(
        string? explicitSource, string? processSource, string? userSource, string expected)
    {
        CliPaths.ResolveSource(explicitSource, processSource, userSource).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", "", "")]
    [InlineData(" ", "\t", "\r\n")]
    public void ResolveSource_NoConfiguredPath_PreservesDefault(
        string? explicitSource, string? processSource, string? userSource)
    {
        CliPaths.ResolveSource(explicitSource, processSource, userSource).ShouldBe(CliPaths.DefaultSource);
    }
}
