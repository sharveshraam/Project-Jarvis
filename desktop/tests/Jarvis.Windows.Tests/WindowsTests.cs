using Jarvis.Windows;

namespace Jarvis.Windows.Tests;

public class HotKeyTests
{
    [Fact]
    public void Parse_CtrlAltJ()
    {
        var (mods, key) = GlobalHotKey.Parse("Ctrl+Alt+J");
        Assert.Equal(0x0003u, mods);
        Assert.Equal(0x4Au, key);
    }

    [Fact]
    public void Parse_Space()
    {
        var (_, key) = GlobalHotKey.Parse("Ctrl+Alt+Space");
        Assert.Equal(0x20u, key);
    }
}

public class StartupTests
{
    [Fact]
    public void StartupKey_CanBeToggled_WithoutAdmin()
    {
        // Per-user HKCU key: must not throw on a normal Windows user account.
        var before = StartupService.IsEnabled();
        StartupService.SetEnabled(before, Environment.ProcessPath ?? "JARVIS.exe");
        Assert.Equal(before, StartupService.IsEnabled());
    }
}
