namespace Termyn.App.Windows.Tests;

/// <summary>
/// The dialog that asks for the day a task has to be finished by.
/// </summary>
/// <remarks>
/// Built rather than shown: what the dialog is asked for, and what it answers, are the two things
/// the window's side of this gets wrong. Showing it would need somebody to click.
/// </remarks>
public class DeadlineFormTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    [WinFormsFact]
    public void It_opens_on_the_deadline_the_task_already_has()
    {
        using var dialog = DeadlineForm.For("Ship it", new DateOnly(2026, 8, 4), Today);

        Assert.Equal(new DateOnly(2026, 8, 4), dialog.Chosen);
    }

    [WinFormsFact]
    public void A_task_with_no_deadline_opens_on_today()
    {
        // The day somebody reaching for this is most often counting from, and never an empty box
        // the picker would have to invent a value for anyway.
        using var dialog = DeadlineForm.For("Ship it", null, Today);

        Assert.Equal(Today, dialog.Chosen);
    }

    [WinFormsFact]
    public void Clearing_answers_with_no_day_at_all()
    {
        using var dialog = DeadlineForm.For("Ship it", new DateOnly(2026, 8, 4), Today);

        dialog.PressClear();

        Assert.Null(dialog.Chosen);
    }

    [WinFormsFact]
    public void There_is_nothing_to_clear_on_a_task_that_hasnt_got_one()
    {
        using var withOne = DeadlineForm.For("Ship it", new DateOnly(2026, 8, 4), Today);
        using var without = DeadlineForm.For("Ship it", null, Today);

        Assert.True(withOne.CanClear);
        Assert.False(without.CanClear);
    }

    [WinFormsFact]
    public void Clearing_closes_the_dialog_as_an_answer_rather_than_a_cancel()
    {
        // Clear is a change like picking a day is, so it leaves by the same door: a cancel would
        // send the caller away thinking nothing had been asked for.
        using var dialog = DeadlineForm.For("Ship it", new DateOnly(2026, 8, 4), Today);

        dialog.PressClear();

        Assert.Equal(DialogResult.OK, dialog.DialogResult);
    }

    [WinFormsFact]
    public void The_task_is_named_above_the_calendar_with_its_own_punctuation()
    {
        // An ampersand in a task's name is a character, not the mark of an accelerator: left on,
        // "Books & Papers" reads "Books Papers" with the P underlined.
        using var dialog = DeadlineForm.For("Books & Papers", null, Today);

        var naming = dialog.Controls.OfType<Label>().Single(l => l.Text == "Books & Papers");

        Assert.False(naming.UseMnemonic);
    }
}
