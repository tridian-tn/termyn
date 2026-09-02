using System.ComponentModel;
using Termyn.Core.Settings;
using Termyn.Presentation;

namespace Termyn.App.Windows;

/// <summary>
/// The comments on a task or a project: the conversation above, a box to add to it below.
/// </summary>
/// <remarks>
/// A list rather than one rendered document, because a comment is a thing you act on — edit it,
/// delete it — and acting on one means being able to say which. Owner-drawn at variable height so a
/// comment of three lines takes three lines; a fixed row would either clip most of them or waste the
/// panel on the short ones.
///
/// Editing happens in the same box that posts, rather than in the row. The panel is short, a row
/// that turns into an editor has whatever height it had, and two ways to type a comment is one more
/// than this needs.
/// </remarks>
internal sealed class CommentsView : UserControl
{
    private const int Gap = 6;
    private const int ComposeLines = 3;

    private readonly ListBox _list;

    /// <summary>
    /// What stands in for the list when there is nothing in it.
    /// </summary>
    /// <remarks>
    /// A control of its own rather than something painted behind the list: the list is docked over
    /// the whole panel and draws its own background, so anything painted under it is never seen.
    /// </remarks>
    private readonly Label _empty;

    private readonly TextBox _compose;
    private readonly Label _hint;
    private readonly LinkLabel _attach;
    private readonly ContextMenuStrip _rowMenu;
    private readonly ToolStripMenuItem _openItem;
    private readonly ToolStripMenuItem _detachItem;
    private readonly ToolStripMenuItem _editItem;
    private readonly ToolStripMenuItem _deleteItem;
    private readonly Panel _composeArea;

    private Theme _theme = Theme.Resolve(ThemePreference.System);
    private IReadOnlyList<CommentRow> _comments = [];

    /// <summary>The comment being rewritten, or null when the box would post a new one.</summary>
    private string? _editing;

    /// <summary>What to say while a file is moving, or null when none is.</summary>
    private string? _progress;

    public CommentsView()
    {
        _list = new ListBox
        {
            Dock = DockStyle.Fill,
            DrawMode = DrawMode.OwnerDrawVariable,
            BorderStyle = BorderStyle.None,
            IntegralHeight = false,
            SelectionMode = SelectionMode.One,
        };
        _list.MeasureItem += OnMeasureItem;
        _list.DrawItem += OnDrawItem;
        _list.KeyDown += OnListKeyDown;
        _list.DoubleClick += (_, _) => BeginEdit();

        _compose = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            AcceptsReturn = true,
            BorderStyle = BorderStyle.FixedSingle,
            ScrollBars = ScrollBars.Vertical,
        };
        _compose.KeyDown += OnComposeKeyDown;

        _hint = new Label
        {
            Dock = DockStyle.Bottom,
            AutoSize = false,
            Height = 18,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _attach = new LinkLabel
        {
            Text = "Attach a file…",
            AutoSize = false,
            Width = 110,
            Height = 18,
            Dock = DockStyle.Right,
            TextAlign = ContentAlignment.MiddleRight,
        };
        _attach.LinkClicked += (_, _) => AttachRequested?.Invoke();
        _hint.Controls.Add(_attach);

        _rowMenu = new ContextMenuStrip();
        _openItem = new ToolStripMenuItem("Open attachment", null, (_, _) => RaiseForSelected(OpenRequested));
        _detachItem = new ToolStripMenuItem("Remove attachment", null, (_, _) => RaiseForSelected(DetachRequested));
        _editItem = new ToolStripMenuItem("Edit", null, (_, _) => BeginEdit());
        _deleteItem = new ToolStripMenuItem("Delete comment", null, (_, _) => RaiseForSelected(Deleted));
        _rowMenu.Items.AddRange([_openItem, _detachItem, new ToolStripSeparator(), _editItem, _deleteItem]);
        _rowMenu.Opening += OnRowMenuOpening;
        _list.ContextMenuStrip = _rowMenu;

        _empty = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Padding = new Padding(Gap),
            Visible = false,
        };

        // Starts hidden so the first CanComment that turns it on is a change, and gives it a height.
        _composeArea = new Panel { Dock = DockStyle.Bottom, Height = 0, Visible = false };
        _composeArea.Controls.Add(_compose);
        _composeArea.Controls.Add(_hint);

        Controls.Add(_empty);
        Controls.Add(_list);
        Controls.Add(_composeArea);

        SetHint();
        ApplyTheme();
    }

    /// <summary>Raised when a new comment is posted, with what it says.</summary>
    public event Action<string>? Posted;

    /// <summary>Raised when an existing comment is rewritten, with its id and its new text.</summary>
    public event Action<string, string>? Edited;

    /// <summary>Raised when a comment is to be removed, with its id.</summary>
    public event Action<string>? Deleted;

    /// <summary>Raised when a comment's file is to be opened, with the comment's id.</summary>
    public event Action<string>? OpenRequested;

    /// <summary>Raised when a comment's file is to be taken off it, with the comment's id.</summary>
    public event Action<string>? DetachRequested;

    /// <summary>Raised when the user asks to put a file on a new comment.</summary>
    public event Action? AttachRequested;

    /// <summary>Raised when a transfer in progress is called off.</summary>
    public event Action? CancelRequested;

    /// <summary>
    /// Escape calls off a transfer, wherever the focus is in the pane.
    /// </summary>
    /// <remarks>
    /// Taken here rather than in either key handler because a download is the one thing in this
    /// window the user waits on, and the key that stops waiting shouldn't depend on whether the
    /// list or the box happens to have the focus. Escape means something else when nothing is
    /// moving, so it's only claimed while something is.
    /// </remarks>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && _progress is not null)
        {
            CancelRequested?.Invoke();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>
    /// Presses Escape at this pane, for a test that has no message loop to press it with.
    /// </summary>
    /// <returns>True when the pane took the key, which is what "a transfer was called off" means</returns>
    internal bool PressEscape()
    {
        var message = Message.Create(Handle, 0, 0, 0);
        return ProcessCmdKey(ref message, Keys.Escape);
    }

    /// <summary>The colours to draw with.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Theme Theme
    {
        get => _theme;
        set
        {
            _theme = value;
            ApplyTheme();
        }
    }

    /// <summary>
    /// What to say when there are no comments, and nothing can be added either — no task selected,
    /// or one the account no longer holds.
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Placeholder
    {
        get => _empty.Text;
        set => _empty.Text = value;
    }

    /// <summary>
    /// Whether a comment can be added at all. False leaves the conversation readable and takes away
    /// the box, rather than offering a box whose typing would be declined.
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool CanComment
    {
        get => _composeArea.Visible;
        set
        {
            if (_composeArea.Visible == value)
                return;

            _composeArea.Visible = value;
            _composeArea.Height = value ? ComposeHeight() : 0;
            _attach.Enabled = value && _progress is null;

            if (!value)
                CancelEdit();
        }
    }

    /// <summary>The comments to show, oldest first. The selected one is kept where it still exists.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<CommentRow> Comments
    {
        get => _comments;
        set
        {
            var selected = SelectedId;

            // Whether the list was sitting on the newest thing said. If it was, it follows the
            // conversation on; if the user had deliberately gone back up it, it stays where they
            // put it — a sync republishes this every forty-five seconds and must not move it.
            //
            // Both halves have to agree before it follows. The selection can sit on the newest
            // comment while the view is nowhere near it, because the wheel and the scrollbar move
            // the view without touching the selection — and following then would drag the highlight
            // off screen, leaving the delete key aimed at a comment nobody can see.
            var onNewest = selected is null || (_comments.Count > 0 && _comments[^1].Id == selected);
            var atEnd = ShowingTheEnd();
            var top = _list.TopIndex;

            _comments = value;

            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                foreach (var comment in value)
                    _list.Items.Add(comment);

                var index = (onNewest && atEnd) || selected is null ? -1 : IndexOf(selected);
                _list.SelectedIndex = index >= 0 ? index : value.Count - 1;

                // Selecting scrolls to what was selected, so the view is put back afterwards. Not
                // when it was already at the end: there it should stay at the end, which is now one
                // comment further down.
                if (!atEnd)
                    _list.TopIndex = Math.Clamp(top, 0, Math.Max(0, value.Count - 1));
            }
            finally
            {
                _list.EndUpdate();
            }

            _list.Visible = value.Count > 0;
            _empty.Visible = value.Count == 0;

            // The one being rewritten has gone — deleted here or on the web while it was open. The
            // box would otherwise still be aimed at it and commit an edit to nothing.
            if (_editing is not null && IndexOf(_editing) < 0)
                CancelEdit();

            _list.Invalidate();
        }
    }

    /// <summary>The comment the list is on, or null when it is on none.</summary>
    public string? SelectedId => _list.SelectedItem is CommentRow row ? row.Id : null;

    /// <summary>
    /// Whether the newest comment is currently in view.
    /// </summary>
    /// <remarks>
    /// Asked of the view rather than of the selection, which is the only way to tell that the user
    /// has scrolled back up the conversation: the wheel and the scrollbar both move the view and
    /// leave the selection where it was.
    /// </remarks>
    private bool ShowingTheEnd()
    {
        if (_list.Items.Count == 0)
            return true;

        // An empty rectangle rather than one below the fold is how the control reports a row that
        // is scrolled out of view altogether — and an empty one has a Top of nought, which reads as
        // the very top of the list if it isn't asked about first.
        var last = _list.GetItemRectangle(_list.Items.Count - 1);
        return !last.IsEmpty && last.Top < _list.ClientSize.Height;
    }

    private int IndexOf(string id)
    {
        for (var i = 0; i < _comments.Count; i++)
            if (_comments[i].Id == id)
                return i;

        return -1;
    }

    private int ComposeHeight() => (_compose.Font.Height * ComposeLines) + _hint.Height + Gap;

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);

        if (_composeArea.Visible)
            _composeArea.Height = ComposeHeight();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);

        // Height comes from wrapping the text to the current width, so a resize re-measures. The
        // list only asks again when its items change, hence the round-trip through the collection.
        if (_list.Items.Count > 0)
            Comments = _comments;
    }

    private void ApplyTheme()
    {
        BackColor = _theme.Panel;
        _list.BackColor = _theme.Panel;
        _list.ForeColor = _theme.Text;
        _compose.BackColor = _theme.Row;
        _compose.ForeColor = _theme.Text;
        _composeArea.BackColor = _theme.Panel;
        _empty.BackColor = _theme.Panel;
        _empty.ForeColor = _theme.Muted;
        _hint.BackColor = _theme.Panel;
        _hint.ForeColor = _theme.Muted;
        _list.Invalidate();
    }

    private void SetHint()
    {
        // A transfer in progress owns this line: it's the only thing here the user is waiting on,
        // and the panel has nowhere else to say so.
        if (_progress is { } busy)
        {
            _hint.Text = busy;
            return;
        }

        _hint.Text = _editing is null
            ? "Ctrl+Enter to post"
            : "Editing — Ctrl+Enter to save, Esc to cancel";
    }

    /// <summary>
    /// What to say while a file is being transferred, or null when nothing is.
    /// </summary>
    /// <remarks>
    /// Attaching is off while one is running. Two at once in a panel with one line to report them
    /// in would leave the user unable to tell which had failed.
    /// </remarks>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? Progress
    {
        get => _progress;
        set
        {
            _progress = value;
            _attach.Enabled = value is null && CanComment;
            SetHint();
        }
    }

    /// <summary>
    /// What's in the compose box, for a file being attached alongside it.
    /// </summary>
    /// <remarks>
    /// Read rather than taken: an upload that fails must leave the typing where it was, so the box
    /// is only cleared once the comment has actually been posted.
    /// </remarks>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Draft => _compose.Text.Trim();

    /// <summary>Empties the compose box, once what was in it has been posted.</summary>
    public void ClearDraft() => CancelEdit();

    /// <summary>Raises an event for whichever comment the list is on, if it's on one.</summary>
    private void RaiseForSelected(Action<string>? raise)
    {
        if (SelectedId is { } id)
            raise?.Invoke(id);
    }

    /// <summary>
    /// Greys what the selected comment can't do, so nothing on the menu only fails.
    /// </summary>
    private void OnRowMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var comment = _list.SelectedItem as CommentRow;
        var attachment = comment?.Attachment;

        // A file still being processed has no url to fetch from yet, so opening it can't work.
        _openItem.Enabled = attachment is { Pending: false, CanFetch: true };
        _detachItem.Enabled = attachment is not null && CanComment;
        _editItem.Enabled = comment is not null && CanComment;
        _deleteItem.Enabled = comment is not null && CanComment;

        e.Cancel = comment is null;
    }

    // ---- Drawing --------------------------------------------------------------------------------

    /// <summary>
    /// The width text wraps to.
    /// </summary>
    /// <remarks>
    /// The scrollbar is always allowed for, whether or not one is showing. Measuring decides how
    /// tall the rows are, how tall they are decides whether a scrollbar is needed, and the scrollbar
    /// takes the width the measuring was done against — so measuring at the width currently
    /// available is a circle whose first pass is wrong. Reserving it throughout costs a slightly
    /// early wrap on a short conversation and is the same answer every time.
    /// </remarks>
    private int TextWidth => Math.Max(
        40,
        _list.ClientSize.Width - (Gap * 2) - SystemInformation.VerticalScrollBarWidth);

    private void OnMeasureItem(object? sender, MeasureItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _comments.Count)
            return;

        var meta = _list.Font.Height;
        var body = TextRenderer.MeasureText(
            e.Graphics,
            BodyOf(_comments[e.Index]),
            _list.Font,
            new Size(TextWidth, int.MaxValue),
            TextFormatFlags.WordBreak).Height;

        e.ItemHeight = meta + body + (Gap * 2);
    }

    private void OnDrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _comments.Count)
            return;

        var comment = _comments[e.Index];
        var selected = (e.State & DrawItemState.Selected) != 0;

        using (var background = new SolidBrush(selected ? _theme.Accent : _theme.Panel))
            e.Graphics.FillRectangle(background, e.Bounds);

        var text = selected ? _theme.OnAccent : _theme.Text;
        var muted = selected ? _theme.OnAccent : _theme.Muted;

        var meta = new Rectangle(e.Bounds.X + Gap, e.Bounds.Y + Gap, TextWidth, _list.Font.Height);
        TextRenderer.DrawText(e.Graphics, MetaOf(comment), _list.Font, meta, muted, TextFormatFlags.EndEllipsis);

        var body = new Rectangle(
            e.Bounds.X + Gap,
            meta.Bottom,
            TextWidth,
            e.Bounds.Height - _list.Font.Height - (Gap * 2));

        TextRenderer.DrawText(e.Graphics, BodyOf(comment), _list.Font, body, text, TextFormatFlags.WordBreak);
    }

    /// <summary>
    /// The line above a comment: when it was posted, and the file on it if it has one.
    /// </summary>
    /// <remarks>
    /// One with no posted time is one this client has only just queued. Saying so is the honest
    /// answer offline, where it may sit unsent for a while.
    /// </remarks>
    private static string MetaOf(CommentRow comment)
    {
        var when = comment.Posted.Length == 0 ? "Not sent yet" : comment.Posted;
        return comment.AttachmentLabel is { } file ? $"{when}   📎 {file}" : when;
    }

    /// <summary>
    /// What a comment says, or a stand-in when it says nothing.
    /// </summary>
    /// <remarks>
    /// A comment can carry a file and no words at all. Drawn from its content alone that is a blank
    /// row, which reads as a comment that failed to load rather than one that is a file.
    /// </remarks>
    private static string BodyOf(CommentRow comment)
    {
        if (comment.Content.Length > 0)
            return comment.Content;

        return comment.AttachmentName is not null ? "(no message)" : "(empty)";
    }

    // ---- Keys -----------------------------------------------------------------------------------

    private void OnComposeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && _editing is not null)
        {
            CancelEdit();
            e.Handled = e.SuppressKeyPress = true;
            return;
        }

        if (e.KeyCode != Keys.Enter || !e.Control)
            return;

        e.Handled = e.SuppressKeyPress = true;
        Commit();
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Delete when SelectedId is { } id:
                e.Handled = e.SuppressKeyPress = true;
                Deleted?.Invoke(id);
                break;

            case Keys.F2:
            case Keys.Enter:
                e.Handled = e.SuppressKeyPress = true;
                BeginEdit();
                break;
        }
    }

    /// <summary>Loads the selected comment into the box, which then saves rather than posts.</summary>
    private void BeginEdit()
    {
        if (!CanComment || _list.SelectedItem is not CommentRow row)
            return;

        _editing = row.Id;
        _compose.Text = row.Content;
        _compose.SelectionStart = _compose.TextLength;
        SetHint();
        _compose.Focus();
    }

    private void CancelEdit()
    {
        _editing = null;
        _compose.Clear();
        SetHint();
    }

    /// <summary>
    /// Sends what is in the box — as an edit when one is open, otherwise as a new comment.
    /// </summary>
    /// <remarks>
    /// Emptied and re-aimed before the event rather than after, because handling it republishes the
    /// list, and a box still holding the text would post it twice on the next Ctrl+Enter.
    /// </remarks>
    private void Commit()
    {
        var text = _compose.Text.Trim();
        if (text.Length == 0)
            return;

        var editing = _editing;
        CancelEdit();

        if (editing is null)
            Posted?.Invoke(text);
        else
            Edited?.Invoke(editing, text);
    }
}
