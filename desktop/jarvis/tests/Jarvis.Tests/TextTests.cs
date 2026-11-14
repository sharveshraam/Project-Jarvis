using Jarvis.Core.Files;
using Jarvis.Core.Shell;
using Jarvis.Core.Text;

namespace Jarvis.Tests;

public class SpeechTextTests
{
    [Fact]
    public void Markdown_and_paths_are_simplified()
    {
        Assert.Equal("Done. Opened report.pdf", SpeechText.ForSpeaking(@"**Done.** Opened `C:\Users\me\Documents\report.pdf`"));
    }

    [Fact]
    public void Links_are_spoken_by_their_label()
    {
        Assert.Equal("See the docs now", SpeechText.ForSpeaking("See [the docs](https://example.com/a) now"));
        Assert.Equal("Visit a link for more", SpeechText.ForSpeaking("Visit https://example.com/page for more"));
    }

    [Fact]
    public void Code_blocks_are_not_read_aloud()
    {
        var spoken = SpeechText.ForSpeaking("Here it is:\n```\nvar x = 1;\n```\nDone");
        Assert.Contains("code omitted", spoken);
        Assert.DoesNotContain("var x", spoken);
        Assert.DoesNotContain("`", spoken);
    }

    [Fact]
    public void Lists_lose_their_bullets_and_ids()
    {
        var spoken = SpeechText.ForSpeaking("Tasks:\n• Buy milk [#3]\n• Call mom [#4]");
        Assert.DoesNotContain("•", spoken);
        Assert.DoesNotContain("#3", spoken);
        Assert.DoesNotContain("\n", spoken);
        Assert.Contains("Buy milk", spoken);
        Assert.Contains("Call mom", spoken);
    }

    [Fact]
    public void Long_replies_are_cut_at_a_sentence_boundary()
    {
        var text = string.Join(" ", Enumerable.Repeat("This is one short sentence.", 60));
        var spoken = SpeechText.ForSpeaking(text, 200);
        Assert.EndsWith("The rest is on screen.", spoken);
        Assert.True(spoken.Length <= 200 + " The rest is on screen.".Length);
        Assert.Contains("sentence. The rest is on screen.", spoken);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_to_say_yields_an_empty_string(string text)
    {
        Assert.Equal("", SpeechText.ForSpeaking(text));
    }
}

public class SettingsTextTests
{
    [Fact]
    public void Lines_are_trimmed_unquoted_and_deduplicated()
    {
        Assert.Equal(new[] { "a", "b" }, SettingsText.Lines("a\r\n\"b\"\n\nA\n"));
        Assert.Empty(SettingsText.Lines(null));
    }

    [Fact]
    public void Aliases_are_parsed_one_per_line()
    {
        var aliases = SettingsText.ParseAliases("code = C:\\Tools\\Code.exe\nbad line\n = x\nnotes=notepad");
        Assert.Equal(2, aliases.Count);
        Assert.Equal(@"C:\Tools\Code.exe", aliases["CODE"]);
        Assert.Equal("notepad", aliases["notes"]);
    }

    [Fact]
    public void Aliases_round_trip()
    {
        var aliases = new Dictionary<string, string> { ["zed"] = "zed.exe", ["Code"] = @"C:\Tools\Code.exe" };
        var parsed = SettingsText.ParseAliases(SettingsText.FormatAliases(aliases));
        Assert.Equal(2, parsed.Count);
        Assert.Equal("zed.exe", parsed["zed"]);
        Assert.Equal(@"C:\Tools\Code.exe", parsed["code"]);
    }

    [Fact]
    public void Approved_commands_are_parsed_with_all_fields()
    {
        var text = "build | dotnet | build \"My App.sln\" -c Release | C:\\src\\app | yes | yes\n"
                   + "# a comment\n"
                   + "test | dotnet | test\n"
                   + "broken\n"
                   + "build | npm | run build";

        var commands = SettingsText.ParseCommands(text, out var errors);

        Assert.Equal(2, commands.Count);
        Assert.Equal("build", commands[0].Name);
        Assert.Equal("dotnet", commands[0].Executable);
        Assert.Equal(new[] { "build", "My App.sln", "-c", "Release" }, commands[0].Arguments);
        Assert.Equal(@"C:\src\app", commands[0].WorkingDirectory);
        Assert.True(commands[0].Trusted);
        Assert.True(commands[0].RunOnDevSetup);

        Assert.Equal("test", commands[1].Name);
        Assert.Equal(new[] { "test" }, commands[1].Arguments);
        Assert.False(commands[1].Trusted);
        Assert.False(commands[1].RunOnDevSetup);

        Assert.Equal(2, errors.Count);
        Assert.StartsWith("Line 4:", errors[0]);
        Assert.StartsWith("Line 5:", errors[1]);
    }

    [Fact]
    public void Approved_commands_round_trip()
    {
        var original = SettingsText.ParseCommands("serve | npm | run dev --port 3000 | C:\\web | no | yes", out _);
        var again = SettingsText.ParseCommands(SettingsText.FormatCommands(original), out var errors);

        Assert.Empty(errors);
        Assert.Single(again);
        Assert.Equal("serve", again[0].Name);
        Assert.Equal(new[] { "run", "dev", "--port", "3000" }, again[0].Arguments);
        Assert.Equal(@"C:\web", again[0].WorkingDirectory);
        Assert.False(again[0].Trusted);
        Assert.True(again[0].RunOnDevSetup);
    }

    [Fact]
    public void Arguments_keep_quoted_segments_together()
    {
        Assert.Equal(new[] { "a", "b c", "", "d" }, SettingsText.SplitArguments("a \"b c\" \"\" d"));
        Assert.Empty(SettingsText.SplitArguments("   "));
        Assert.Equal("a \"b c\" \"\"", SettingsText.JoinArguments(new[] { "a", "b c", "" }));
    }
}

public class RelevanceTests
{
    [Fact]
    public void Tokens_drop_stop_words_and_short_words()
    {
        var tokens = Relevance.Tokens("What is the deadline for my thesis?");
        Assert.Contains("deadline", tokens);
        Assert.Contains("thesis", tokens);
        Assert.DoesNotContain("what", tokens);
        Assert.DoesNotContain("the", tokens);
        Assert.DoesNotContain("is", tokens);
    }

    [Fact]
    public void Related_text_scores_higher_than_unrelated_text()
    {
        var tokens = Relevance.Tokens("What is the deadline for my thesis?");
        Assert.Equal(4, Relevance.Score(tokens, "Thesis deadline is 30 November"));
        Assert.Equal(0, Relevance.Score(tokens, "Likes filter coffee"));
        Assert.Equal(0, Relevance.Score(new HashSet<string>(), "anything"));
    }

    [Fact]
    public void Truncation_adds_an_ellipsis()
    {
        Assert.Equal("abc…", Relevance.Truncate("abcdef", 4));
        Assert.Equal("abc", Relevance.Truncate("abc", 4));
    }
}

public class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Alt+J", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x4Au, "Ctrl+Alt+J")]
    [InlineData("ctrl + shift + f5", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x74u, "Ctrl+Shift+F5")]
    [InlineData("Alt+Space", HotkeyModifiers.Alt, 0x20u, "Alt+Space")]
    [InlineData("Win+Shift+1", HotkeyModifiers.Win | HotkeyModifiers.Shift, 0x31u, "Shift+Win+1")]
    [InlineData("F9", HotkeyModifiers.None, 0x78u, "F9")]
    [InlineData("Control+Alt+Space", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x20u, "Ctrl+Alt+Space")]
    public void Valid_shortcuts_are_parsed(string text, HotkeyModifiers modifiers, uint virtualKey, string display)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture, out var error), error);
        Assert.Equal(modifiers, gesture.Modifiers);
        Assert.Equal(virtualKey, gesture.VirtualKey);
        Assert.Equal(display, gesture.Display);
    }

    [Theory]
    [InlineData("")]
    [InlineData("J")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+Banana")]
    [InlineData("Ctrl+Alt+Delete")]
    public void Unusable_shortcuts_are_rejected_with_a_reason(string text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out _, out var error));
        Assert.NotEqual("", error);
    }

    [Fact]
    public void Default_shortcuts_are_valid()
    {
        var settings = new Jarvis.Core.Models.AppSettings();
        Assert.True(HotkeyGesture.TryParse(settings.ToggleHotkey, out _, out _));
        Assert.True(HotkeyGesture.TryParse(settings.PushToTalkHotkey, out _, out _));
        Assert.NotEqual(settings.ToggleHotkey, settings.PushToTalkHotkey);
    }
}

public class FileCategorizerTests
{
    [Theory]
    [InlineData("holiday.JPG", "Images")]
    [InlineData("thesis.pdf", "Documents")]
    [InlineData("lecture.mp4", "Videos")]
    [InlineData("song.flac", "Audio")]
    [InlineData("backup.zip", "Archives")]
    [InlineData("setup.msi", "Installers")]
    [InlineData("Program.cs", "Code")]
    [InlineData("mystery.xyz", "Other")]
    [InlineData("README", "Other")]
    public void Files_are_grouped_by_extension(string name, string category)
    {
        Assert.Equal(category, FileCategorizer.CategoryFor(name));
    }

    [Theory]
    [InlineData("movie.mkv.crdownload", true)]
    [InlineData("data.part", true)]
    [InlineData("desktop.ini", true)]
    [InlineData(".gitignore", true)]
    [InlineData("report.pdf", false)]
    public void Unfinished_downloads_and_hidden_files_are_left_alone(string name, bool transient)
    {
        Assert.Equal(transient, FileCategorizer.IsTransient(name));
    }

    [Theory]
    [InlineData(512L, "512 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(5L * 1024 * 1024, "5 MB")]
    public void Sizes_are_human_readable(long bytes, string expected)
    {
        Assert.Equal(expected, FileService.FormatSize(bytes));
    }
}
