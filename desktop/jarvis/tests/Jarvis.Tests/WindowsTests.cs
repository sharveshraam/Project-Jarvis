using System.Text;
using Jarvis.Windows;

namespace Jarvis.Tests;

/// <summary>Windows-specific pieces that can be exercised without touching the user's real profile.</summary>
public class DpapiSecretStoreTests : IDisposable
{
    private const string Key = "AIzaSy-test-key-0123456789";
    private readonly TempFolder _temp = new TempFolder();
    private readonly string _path;

    public DpapiSecretStoreTests() => _path = Path.Combine(_temp.Root, "secrets.dat");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void A_secret_survives_a_restart()
    {
        new DpapiSecretStore(_path).Set("gemini_api_key", Key);

        Assert.Equal(Key, new DpapiSecretStore(_path).Get("gemini_api_key"));
    }

    [Fact]
    public void The_file_on_disk_never_contains_the_secret_in_clear_text()
    {
        new DpapiSecretStore(_path).Set("gemini_api_key", Key);

        var bytes = File.ReadAllBytes(_path);
        Assert.DoesNotContain(Key, Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain(Key, Encoding.Unicode.GetString(bytes));
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(Key)), Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void A_missing_secret_is_null()
    {
        Assert.Null(new DpapiSecretStore(_path).Get("gemini_api_key"));
    }

    [Fact]
    public void A_removed_secret_is_gone_after_a_restart()
    {
        var store = new DpapiSecretStore(_path);
        store.Set("gemini_api_key", Key);
        store.Set("other", "value");

        store.Remove("gemini_api_key");

        var reopened = new DpapiSecretStore(_path);
        Assert.Null(reopened.Get("gemini_api_key"));
        Assert.Equal("value", reopened.Get("other"));
    }

    [Fact]
    public void Replacing_a_secret_keeps_only_the_new_value()
    {
        var store = new DpapiSecretStore(_path);
        store.Set("gemini_api_key", Key);
        store.Set("gemini_api_key", "second-value");

        Assert.Equal("second-value", new DpapiSecretStore(_path).Get("gemini_api_key"));
    }

    [Fact]
    public void A_corrupted_file_reads_as_no_secret_instead_of_crashing()
    {
        File.WriteAllText(_path, "this is not a secret store");

        var store = new DpapiSecretStore(_path);
        Assert.Null(store.Get("gemini_api_key"));

        store.Set("gemini_api_key", Key);
        Assert.Equal(Key, new DpapiSecretStore(_path).Get("gemini_api_key"));
    }
}

public class StartMenuLookupTests : IDisposable
{
    private readonly TempFolder _temp = new TempFolder();

    public void Dispose() => _temp.Dispose();

    private string[] Roots(params string[] shortcuts)
    {
        foreach (var shortcut in shortcuts) _temp.File(Path.Combine("Programs", shortcut));
        return new[] { Path.Combine(_temp.Root, "Programs"), Path.Combine(_temp.Root, "MissingRoot") };
    }

    [Fact]
    public void An_exact_name_wins_over_a_longer_one()
    {
        var roots = Roots("Spotify.lnk", Path.Combine("Tools", "Spotify Helper.lnk"));

        Assert.Equal("Spotify.lnk", Path.GetFileName(AppController.FindStartMenuShortcut("spotify", roots)));
    }

    [Fact]
    public void A_prefix_matches_when_there_is_no_exact_name()
    {
        var roots = Roots(Path.Combine("JetBrains", "Rider 2026.2.lnk"), "Paint.lnk");

        Assert.Equal("Rider 2026.2.lnk", Path.GetFileName(AppController.FindStartMenuShortcut("Rider", roots)));
    }

    [Fact]
    public void A_name_inside_the_title_matches_only_when_it_is_long_enough()
    {
        var roots = Roots("Microsoft Teams.lnk");

        Assert.Equal("Microsoft Teams.lnk", Path.GetFileName(AppController.FindStartMenuShortcut("teams", roots)));
        Assert.Null(AppController.FindStartMenuShortcut("ams", roots));
    }

    [Fact]
    public void Uninstallers_are_never_chosen()
    {
        var roots = Roots("Uninstall Spotify.lnk", "Spotify Uninstaller.lnk");

        Assert.Null(AppController.FindStartMenuShortcut("spotify", roots));
        Assert.Null(AppController.FindStartMenuShortcut("uninstall spotify", roots));
    }

    [Fact]
    public void Spacing_punctuation_and_case_are_ignored()
    {
        var roots = Roots("Visual Studio Code.lnk");

        Assert.Equal("Visual Studio Code.lnk", Path.GetFileName(AppController.FindStartMenuShortcut("visual-studio code", roots)));
    }

    [Fact]
    public void An_unknown_or_too_short_name_finds_nothing()
    {
        var roots = Roots("Spotify.lnk");

        Assert.Null(AppController.FindStartMenuShortcut("photoshop", roots));
        Assert.Null(AppController.FindStartMenuShortcut("s", roots));
        Assert.Null(AppController.FindStartMenuShortcut("spotify", new[] { Path.Combine(_temp.Root, "MissingRoot") }));
    }
}
