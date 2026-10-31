using Termyn.Core.Attachments;

namespace Termyn.Core.Tests;

/// <summary>
/// The name a saved copy of an attachment goes by.
/// </summary>
/// <remarks>
/// It's the account's name for the file, which is the whole point of saving rather than opening, so
/// what matters is that as little of it as possible changes, and that what does change is only what
/// would otherwise put the copy somewhere else or open a device.
/// </remarks>
public sealed class DownloadNameTests
{
    [Theory]
    [InlineData("agenda.pdf", "agenda.pdf")]
    [InlineData("Résumé – final draft.docx", "Résumé – final draft.docx")]
    [InlineData("notes (1).txt", "notes (1).txt")]
    public void An_ordinary_name_is_kept_exactly(string name, string expected)
        => Assert.Equal(expected, DownloadName.Safe(name));

    [Theory]
    [InlineData(@"..\..\evil.exe", ".._.._evil.exe")]
    [InlineData("../../evil.exe", ".._.._evil.exe")]
    [InlineData(@"C:\Windows\win.ini", "C__Windows_win.ini")]
    public void A_name_carrying_a_path_cannot_lead_anywhere_else(string name, string expected)
        => Assert.Equal(expected, DownloadName.Safe(name));

    [Theory]
    [InlineData("what? why*.txt", "what_ why_.txt")]
    [InlineData("a<b>c|d\"e.txt", "a_b_c_d_e.txt")]
    [InlineData("tab\there.txt", "tab_here.txt")]
    public void What_windows_refuses_in_a_name_becomes_an_underscore(string name, string expected)
        => Assert.Equal(expected, DownloadName.Safe(name));

    [Theory]
    [InlineData("notes.", "notes")]
    [InlineData("notes . .", "notes")]
    public void Trailing_dots_and_spaces_go_as_windows_would_drop_them_anyway(string name, string expected)
        => Assert.Equal(expected, DownloadName.Safe(name));

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("Com1.tar.gz", "_Com1.tar.gz")]
    [InlineData("LPT¹", "_LPT¹")]
    [InlineData("aux .log", "_aux .log")]
    public void A_device_name_is_moved_off_the_device(string name, string expected)
        => Assert.Equal(expected, DownloadName.Safe(name));

    [Theory]
    [InlineData("console.txt")]
    [InlineData("com10.txt")]
    [InlineData("my.con")]
    public void A_name_that_only_starts_like_a_device_is_left_alone(string name)
        => Assert.Equal(name, DownloadName.Safe(name));

    [Theory]
    [InlineData("")]
    [InlineData("...")]
    [InlineData("  ")]
    public void A_name_with_nothing_usable_in_it_still_gets_one(string name)
        => Assert.Equal("attachment", DownloadName.Safe(name));

    [Theory]
    [InlineData("invoice‮fdp.exe", "invoicefdp.exe")]       // reads as "invoiceexe.pdf" when shown
    [InlineData("⁧report⁩.exe", "report.exe")]
    [InlineData("zero​width﻿.txt", "zerowidth.txt")]
    public void Invisible_characters_that_change_how_a_name_reads_are_taken_out(string name, string expected)
        => Assert.Equal(expected, DownloadName.Safe(name));

    [Theory]
    [InlineData("del\u007Fete.txt", "del_ete.txt")]
    [InlineData("c1\u0085control.txt", "c1_control.txt")]
    public void Every_control_character_becomes_an_underscore(string name, string expected)
        => Assert.Equal(expected, DownloadName.Safe(name));

    [Fact]
    public void A_very_long_name_is_shortened_and_keeps_its_extension()
    {
        // Past 255 a name can't be made at all, and a long one under that can still run a path past
        // what the system allows.
        var safe = DownloadName.Safe(new string('a', 300) + ".pdf");

        Assert.Equal(new string('a', 120) + ".pdf", safe);
    }

    [Fact]
    public void A_name_is_never_cut_between_the_halves_of_a_character()
    {
        // Each of these is two UTF-16 units, so the cut at 120 would fall in the middle of one.
        var safe = DownloadName.Safe("a" + string.Concat(Enumerable.Repeat("\U0001F600", 100)) + ".txt");

        var stem = safe[..^".txt".Length];
        Assert.Equal(119, stem.Length);
        Assert.False(char.IsHighSurrogate(stem[^1]));
        Assert.EndsWith(".txt", safe, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_whose_extension_is_itself_too_long_is_shortened_whole()
    {
        var safe = DownloadName.Safe("notes." + new string('x', 300));

        Assert.Equal(120, safe.Length);
        Assert.StartsWith("notes.", safe, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("agenda.pdf", 0, "agenda.pdf")]
    [InlineData("agenda.pdf", 1, "agenda (1).pdf")]
    [InlineData("agenda.pdf", 12, "agenda (12).pdf")]
    [InlineData("README", 2, "README (2)")]
    [InlineData(".gitignore", 1, ".gitignore (1)")]
    [InlineData("archive.tar.gz", 1, "archive.tar (1).gz")]
    public void A_taken_name_is_numbered_before_its_extension(string name, int number, string expected)
        => Assert.Equal(expected, DownloadName.Numbered(name, number));
}
