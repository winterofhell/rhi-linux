using RhiLinux.Steam;

namespace RhiLinux.Tests;

public sealed class VdfParserTests
{
    [Fact]
    public void ParsesModernNestedAndEscapedValues()
    {
        var value = VdfParser.Parse("""
            "libraryfolders"
            {
                "0" { "path" "/games/Steam" "apps" { "42" "1" } }
                "1" { "path" "D:\\Steam Library" }
            }
            """);
        var libraries = value.GetObject("libraryfolders")!;
        Assert.Equal("/games/Steam", libraries.GetObject("0")!.GetString("path"));
        Assert.Equal("D:\\Steam Library", libraries.GetObject("1")!.GetString("path"));
    }

    [Fact]
    public void ParsesLegacyUnquotedValuesAndComments()
    {
        var value = VdfParser.Parse("AppState { appid 42 // comment\n name \"A Game\" }");
        Assert.Equal("42", value.GetObject("AppState")!.GetString("appid"));
    }

    [Fact]
    public void RejectsMalformedInput() => Assert.Throws<FormatException>(() => VdfParser.Parse("\"root\" { \"key\""));
}
