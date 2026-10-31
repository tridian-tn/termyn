using Termyn.Presentation;

namespace Termyn.App.Mac;

/// <summary>
/// The sheet listing the changes Todoist refused for good: what each was, what Todoist said, and a
/// Dismiss that forgets it.
/// </summary>
/// <remarks>
/// No retry, as on Windows: a change Todoist has refused five times will be refused a sixth, and
/// the way forward is to make it again, differently, once it's been dismissed.
/// </remarks>
internal sealed class FailedChangesController(MainPresenter presenter, TaskCommands commands) : NSViewController
{
    private readonly NSStackView _list = new()
    {
        Orientation = NSUserInterfaceLayoutOrientation.Vertical,
        Alignment = NSLayoutAttribute.Leading,
        Spacing = 14,
    };

    public override void LoadView()
    {
        var title = NSTextField.CreateLabel(Strings.FailedTitle);
        title.Font = NSFont.BoldSystemFontOfSize(NSFont.SystemFontSize + 2)!;

        _list.TranslatesAutoresizingMaskIntoConstraints = false;
        var document = new FlippedView();
        document.AddSubview(_list);
        var scroll = new NSScrollView
        {
            DocumentView = document,
            HasVerticalScroller = true,
            AutohidesScrollers = true,
            DrawsBackground = false,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };

        var done = NSButton.CreateButton(Strings.Done, () => DismissController(this));
        done.KeyEquivalent = "\r";

        var buttons = NSStackView.FromViews([new NSView(), done]);
        buttons.Orientation = NSUserInterfaceLayoutOrientation.Horizontal;

        var view = NSStackView.FromViews([title, scroll, buttons]);
        view.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        view.Alignment = NSLayoutAttribute.Leading;
        view.Spacing = 12;
        view.EdgeInsets = new NSEdgeInsets(20, 20, 20, 20);

        NSLayoutConstraint.ActivateConstraints(
        [
            view.WidthAnchor.ConstraintEqualTo(520),
            scroll.HeightAnchor.ConstraintEqualTo(260),
            scroll.WidthAnchor.ConstraintEqualTo(view.WidthAnchor, 1, -40),
            buttons.WidthAnchor.ConstraintEqualTo(view.WidthAnchor, 1, -40),
            _list.TopAnchor.ConstraintEqualTo(document.TopAnchor),
            _list.LeadingAnchor.ConstraintEqualTo(document.LeadingAnchor),
            _list.WidthAnchor.ConstraintEqualTo(scroll.ContentView.WidthAnchor),
            document.WidthAnchor.ConstraintEqualTo(scroll.ContentView.WidthAnchor),
            document.BottomAnchor.ConstraintEqualTo(_list.BottomAnchor),
        ]);

        View = view;
        Fill();
    }

    /// <summary>Lists what's failed, or closes when there's nothing left to list.</summary>
    private void Fill()
    {
        foreach (var old in _list.ArrangedSubviews)
            old.RemoveFromSuperview();

        var failed = presenter.FailedChanges;
        if (failed.Count == 0 && PresentingViewController is not null)
        {
            DismissController(this);
            return;
        }

        foreach (var change in failed)
        {
            var what = NSTextField.CreateWrappingLabel(change.ToString());
            what.Font = NSFont.BoldSystemFontOfSize(NSFont.SystemFontSize)!;

            var why = NSTextField.CreateWrappingLabel(change.Explanation);
            why.TextColor = NSColor.SecondaryLabel;
            why.Font = NSFont.SystemFontOfSize(NSFont.SmallSystemFontSize)!;

            var words = NSStackView.FromViews([what, why]);
            words.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
            words.Alignment = NSLayoutAttribute.Leading;
            words.Spacing = 3;
            words.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
            what.PreferredMaxLayoutWidth = why.PreferredMaxLayoutWidth = 380;

            var uuid = change.Uuid;
            var dismiss = NSButton.CreateButton(Strings.FailedDismiss, () =>
            {
                commands.Guarded(() => presenter.DismissFailure(uuid));
                Fill();
            });
            dismiss.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);

            var row = NSStackView.FromViews([words, dismiss]);
            row.Orientation = NSUserInterfaceLayoutOrientation.Horizontal;
            row.Alignment = NSLayoutAttribute.Top;
            row.Spacing = 12;
            _list.AddArrangedSubview(row);
            row.WidthAnchor.ConstraintEqualTo(_list.WidthAnchor).Active = true;
        }
    }

    /// <summary>A document view whose origin is at the top, so the list starts there.</summary>
    private sealed class FlippedView : NSView
    {
        public override bool IsFlipped => true;
    }
}
