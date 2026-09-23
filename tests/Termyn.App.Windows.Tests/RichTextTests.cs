using System.Text;
using Termyn.Core.Settings;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The document both halves of the description panel hand their control.
/// </summary>
public class RichTextTests
{
    [WinFormsTheory]
    [InlineData("Wei{rd} face")]
    [InlineData(@"Back\slash")]
    [InlineData("Semi;colon")]
    [InlineData(@"Ends in a backslash\")]
    [InlineData("Close}brace")]
    [InlineData(@"Face;}{\f1\fnil Arial;}")]
    [InlineData("Ｍ Ｓ ゴシック")]
    public void A_faces_name_cannot_break_the_document_it_names(string face)
    {
        // The name goes into the font table, which has syntax of its own. Written as it stands, a
        // closing brace ends the table early and a name can close its own entry and declare the next
        // face itself — both of which leave code drawn in something other than the fixed-width face.
        var rtf = new StringBuilder();
        RichText.Open(rtf, face, Theme.Resolve(ThemePreference.Light));
        rtf.Append(@"\f0\fs18 before \f1 code\f0  after\par}");

        using var box = new RichTextBox();
        box.CreateControl();
        RichText.Load(box, rtf);

        Assert.Equal("before code after", box.Text.TrimEnd('\n'));

        box.Select("before ".Length, "code".Length);
        Assert.Equal(FontFamily.GenericMonospace.Name, box.SelectionFont?.Name);
    }
}
