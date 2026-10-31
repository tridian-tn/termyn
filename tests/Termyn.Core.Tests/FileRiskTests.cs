using Termyn.Core.Attachments;

namespace Termyn.Core.Tests;

/// <summary>
/// Which attachments run something when they're opened, so the window asks before opening them.
/// </summary>
/// <remarks>
/// A name is whatever a collaborator called the file, so what matters is that it's judged the way
/// the desktop will read it rather than the way it looks.
/// </remarks>
public sealed class FileRiskTests
{
    [Theory]
    [InlineData("invoice.exe")]
    [InlineData("invoice.scr")]
    [InlineData("setup.msi")]
    [InlineData("run.bat")]
    [InlineData("run.cmd")]
    [InlineData("script.vbs")]
    [InlineData("script.js")]
    [InlineData("script.ps1")]
    [InlineData("page.hta")]
    [InlineData("shortcut.lnk")]
    [InlineData("link.url")]
    [InlineData("tweak.reg")]
    [InlineData("deploy.appref-ms")]
    [InlineData("settings.settingcontent-ms")]
    [InlineData("tool.jar")]
    public void A_windows_program_or_script_runs_code(string name)
        => Assert.True(FileRisk.RunsCode(name));

    [Theory]
    [InlineData("Termyn.app")]
    [InlineData("installer.pkg")]
    [InlineData("disk.dmg")]
    [InlineData("go.command")]
    [InlineData("go.terminal")]
    [InlineData("go.workflow")]
    [InlineData("go.scpt")]
    [InlineData("go.applescript")]
    [InlineData("link.webloc")]
    [InlineData("build.sh")]
    [InlineData("tool.py")]
    public void A_mac_application_or_script_runs_code(string name)
        => Assert.True(FileRisk.RunsCode(name));

    [Theory]
    [InlineData("agenda.pdf")]
    [InlineData("notes.txt")]
    [InlineData("photo.jpeg")]
    [InlineData("report.docx")]
    [InlineData("archive.zip")]
    [InlineData("README")]
    [InlineData(".gitignore")]
    [InlineData("")]
    public void A_document_does_not(string name)
        => Assert.False(FileRisk.RunsCode(name));

    [Theory]
    [InlineData("INVOICE.EXE")]
    [InlineData("Invoice.Scr")]
    public void Case_does_not_hide_it(string name)
        => Assert.True(FileRisk.RunsCode(name));

    [Theory]
    [InlineData("invoice.exe.")]
    [InlineData("invoice.exe  ")]
    [InlineData("invoice.exe . .")]
    public void Trailing_dots_and_spaces_do_not_hide_it(string name)
        => Assert.True(FileRisk.RunsCode(name));

    [Fact]
    public void A_right_to_left_override_does_not_hide_it()
    {
        // Shown, this reads as "invoiceexe.pdf". Its real extension is the one the desktop goes by.
        Assert.True(FileRisk.RunsCode("invoice\u202Efdp.exe"));
        Assert.True(FileRisk.RunsCode("invoice.e\u200Bxe"));
    }

    [Theory]
    [InlineData("/home/someone/.cache/termyn/attachments/3fa2c1.exe")]
    [InlineData(@"C:\Users\someone\AppData\Local\Termyn\attachments\3fa2c1.exe")]
    [InlineData("/Users/someone/Downloads/Termyn.app/")]
    public void A_path_is_judged_by_the_file_at_the_end_of_it(string path)
        => Assert.True(FileRisk.RunsCode(path));

    [Fact]
    public void A_folder_with_a_dot_in_its_name_does_not_lend_the_file_its_extension()
        => Assert.False(FileRisk.RunsCode("/home/someone/run.exe/notes"));
}
