using Xunit;

namespace SteamShortcutsImporter.Tests;

public class GameVariableExpansionTests
{
    [Theory]
    [InlineData("--fullscreen \"{ImagePath}\"", "{ImagePath}")]
    [InlineData("{EmulatorDir}\\Ryujinx.exe", "{EmulatorDir}")]
    [InlineData("{name}", "{name}")]
    public void FindUnexpandedGameVariable_ReturnsPlayniteVariable(string value, string expected)
    {
        Assert.Equal(expected, EmulatorPathUtils.FindUnexpandedGameVariable(value));
    }

    [Fact]
    public void FindUnexpandedGameVariable_IgnoresResolvedAndUnrelatedBraces()
    {
        Assert.Null(EmulatorPathUtils.FindUnexpandedGameVariable(
            @"--fullscreen ""C:\TestGames\Switch\game.nsp"" --format {json}"));
    }
}
