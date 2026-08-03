using System.Text;
using RhiLinux.Core;

namespace RhiLinux.Tests;

public sealed class StateAndIniTests
{
    [Fact]
    public async Task StateRoundTripsWithVersionAndOverride()
    {
        using var temp = new TestDirectory(); var store = new JsonStateStore(temp.Combine("data", "state.json"));
        var state = new ApplicationState(); state.Overrides[42] = new("game.exe", "bin");
        state.ArtifactReferencesByAppId[42] = [new string('a', 64), new string('b', 64)];
        await store.SaveAsync(state);
        var loaded = await store.LoadAsync();
        Assert.Equal(ApplicationState.CurrentSchemaVersion, loaded.SchemaVersion); Assert.Equal("game.exe", loaded.Overrides[42].Executable);
        Assert.Equal(state.ArtifactReferencesByAppId[42], loaded.ArtifactReferencesByAppId[42]);
        Assert.Empty(Directory.EnumerateFiles(temp.Combine("data"), "*.tmp"));
    }

    [Fact]
    public void IniEditingPreservesValuesAndIsCaseInsensitive()
    {
        var ini = IniDocument.Parse("Existing=keep\n[ADDON]\nAddonPath=.\\addons\n");
        ini.Set(string.Empty, "LoadReshade", "true"); ini.Set("addon", "AddonPath", ".\\new");
        Assert.Equal("true", ini.Get(string.Empty, "loadreshade")); Assert.Equal(".\\new", ini.Get("ADDON", "AddonPath")); Assert.Contains("Existing=keep", ini.ToString());
    }

    [Fact]
    public void IniEditingDeduplicatesKeysAcrossRepeatedSectionsAndRejectsMalformedHeaders()
    {
        var ini = IniDocument.Parse("[Plugins]\nLoadReshade=false\nKeep=one\n[Plugins]\nLoadReshade=auto\nKeepTwo=two\n");

        ini.Set("Plugins", "LoadReshade", "true");

        Assert.Equal(1, ini.ValueCount("Plugins", "LoadReshade"));
        Assert.Equal("true", ini.Get("Plugins", "LoadReshade"));
        Assert.Contains("Keep=one", ini.ToString(), StringComparison.Ordinal);
        Assert.Contains("KeepTwo=two", ini.ToString(), StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => IniDocument.ParseStrict("[Plugins\nLoadReshade=true\n"));
    }

    [Fact]
    public void TolerantIniPreservesMalformedUnrelatedLinesAndEditsManagedKeys()
    {
        var original = "\uFEFF[Plugins]\nLoadReshade=false\n; user comment\n[Broken\nLegacy=1\nUnknown=keep\n";
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(original.TrimStart('\uFEFF'))).ToArray();
        var ini = IniDocument.Parse(bytes);
        Assert.True(ini.HadUtf8Bom);
        Assert.True(ini.HasRecoverableIssues);
        Assert.True(ini.CanSafelyEditManagedKeys([("Plugins", "LoadReshade"), ("Plugins", "LoadAsiPlugins")]));
        ini.Set("Plugins", "LoadReshade", "true");
        ini.Set("Plugins", "LoadAsiPlugins", "true");
        Assert.Equal("true", ini.Get("Plugins", "LoadReshade"));
        Assert.Equal("true", ini.Get("Plugins", "LoadAsiPlugins"));
        Assert.Contains("Broken", ini.ToString(), StringComparison.Ordinal);
        Assert.Contains("Unknown=keep", ini.ToString(), StringComparison.Ordinal);
        Assert.Contains("; user comment", ini.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedManagedSectionIsNotConsideredSafelyEditable()
    {
        var ini = IniDocument.Parse("[Plugins\nLoadReshade=false\n");
        Assert.False(ini.CanSafelyEditManagedKeys([("Plugins", "LoadReshade")]));
    }

    [Fact]
    public void XdgPathsUseOverridesWithoutMachineSpecificDefaults()
    {
        var paths = new XdgPaths("/virtual/home", new Dictionary<string, string?> { ["XDG_DATA_HOME"] = "/virtual/data" });
        Assert.Equal("/virtual/data/rhi-linux", paths.AppDataDirectory);
    }
}
