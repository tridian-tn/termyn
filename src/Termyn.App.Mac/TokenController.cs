using CoreGraphics;
using Termyn.Core.Logging;
using Termyn.Presentation;

namespace Termyn.App.Mac;

/// <summary>
/// Asks for the Todoist API token: on a first launch, in a window of its own; when Todoist has
/// turned the saved one away, as a sheet over the main window.
/// </summary>
/// <remarks>
/// Drives <see cref="AuthPresenter"/> the way the Windows token form does, in the same two modes.
/// A first launch checks the token and stores it. A reconnect only checks it, and hands back whose
/// it is, since what happens next turns on that: the same account goes straight on with what it
/// was holding, and another account's is a switch that has to be asked about first.
///
/// What goes wrong is said under the field rather than in an alert, since the next thing to do is
/// always to fix what's in the field.
/// </remarks>
internal sealed class TokenController(AuthPresenter auth, ILog log, bool reconnecting) : NSViewController
{
    private const string IntegrationsUrl = "https://app.todoist.com/app/settings/integrations/developer";

    private readonly NSSecureTextField _field = new() { PlaceholderString = Strings.TokenPlaceholder };
    private readonly NSTextField _problem = NSTextField.CreateWrappingLabel(string.Empty);
    private readonly NSProgressIndicator _spinner = new()
    {
        Style = NSProgressIndicatorStyle.Spinning,
        ControlSize = NSControlSize.Small,
        IsDisplayedWhenStopped = false,
    };

    private NSButton _connect = null!;

    /// <summary>A first launch's token, checked and stored.</summary>
    public event Action? Connected;

    /// <summary>A reconnect's token, checked and not stored, with whose it is.</summary>
    public event Action<TokenCheck>? Accepted;

    /// <summary>A reconnect given up on.</summary>
    public event Action? Cancelled;

    public override void LoadView()
    {
        var icon = NSImageView.FromImage(NSApplication.SharedApplication.ApplicationIconImage);
        icon.TranslatesAutoresizingMaskIntoConstraints = false;
        icon.WidthAnchor.ConstraintEqualTo(64).Active = true;
        icon.HeightAnchor.ConstraintEqualTo(64).Active = true;

        var heading = NSTextField.CreateLabel(reconnecting ? Strings.ReconnectHeading : Strings.TokenHeading);
        heading.Font = NSFont.BoldSystemFontOfSize(NSFont.SystemFontSize + 2)!;

        var explanation = NSTextField.CreateWrappingLabel(reconnecting ? Strings.ReconnectExplanation : Strings.TokenExplanation);
        explanation.TextColor = NSColor.SecondaryLabel;
        explanation.PreferredMaxLayoutWidth = 340;

        var words = Stack(NSUserInterfaceLayoutOrientation.Vertical, 4, heading, explanation);
        words.Alignment = NSLayoutAttribute.Leading;

        var top = Stack(NSUserInterfaceLayoutOrientation.Horizontal, 16, icon, words);
        top.Alignment = NSLayoutAttribute.Top;

        _field.Changed += (_, _) => _connect.Enabled = _field.StringValue.Trim().Length > 0;

        _problem.TextColor = NSColor.SystemRed;
        _problem.Font = NSFont.SystemFontOfSize(NSFont.SmallSystemFontSize)!;
        _problem.Hidden = true;

        var where = NSButton.CreateButton(Strings.TokenWhere, () => NSWorkspace.SharedWorkspace.OpenUrl(new NSUrl(IntegrationsUrl)));
        where.Bordered = false;
        where.ContentTintColor = NSColor.Link;

        // Return presses it, which is what a key equivalent of \r makes a button: the default one,
        // drawn in the accent colour.
        _connect = NSButton.CreateButton(Strings.TokenConnect, OnConnect);
        _connect.KeyEquivalent = "\r";
        _connect.Enabled = false;

        var row = new List<NSView> { where, new NSView(), _spinner };
        if (reconnecting)
        {
            // Esc presses Cancel, as it does in every sheet.
            var cancel = NSButton.CreateButton(Strings.Cancel, () => Cancelled?.Invoke());
            cancel.KeyEquivalent = "\u001b";
            row.Add(cancel);
        }

        row.Add(_connect);
        var buttons = Stack(NSUserInterfaceLayoutOrientation.Horizontal, 8, [.. row]);

        var content = Stack(NSUserInterfaceLayoutOrientation.Vertical, 12, top, _field, _problem, buttons);
        content.Alignment = NSLayoutAttribute.Leading;
        content.EdgeInsets = new NSEdgeInsets(20, 20, 20, 20);
        foreach (var fill in new NSView[] { _field, _problem, buttons })
            fill.WidthAnchor.ConstraintEqualTo(content.WidthAnchor, 1, -40).Active = true;
        content.WidthAnchor.ConstraintEqualTo(460).Active = true;

        View = content;
    }

    public override void ViewDidAppear()
    {
        base.ViewDidAppear();
        View.Window?.MakeFirstResponder(_field);
    }

    /// <summary>The first-launch window, fixed in size, centred, with this as its content.</summary>
    public NSWindow InWindow()
    {
        var window = new NSWindow(new CGRect(0, 0, 460, 200), NSWindowStyle.Titled | NSWindowStyle.Closable, NSBackingStore.Buffered, deferCreation: false)
        {
            Title = Strings.TokenWindowTitle,
            ContentViewController = this,
        };
        window.Center();
        return window;
    }

    private async void OnConnect()
    {
        _problem.Hidden = true;
        Busy(true);

        try
        {
            if (reconnecting)
            {
                var check = await auth.ValidateAsync(_field.StringValue);
                if (check.Result == TokenValidationResult.Valid)
                {
                    Accepted?.Invoke(check);
                    return;
                }

                Refused(check.Result);
            }
            else
            {
                var result = await auth.ValidateAndStoreAsync(_field.StringValue);
                if (result == TokenValidationResult.Valid)
                {
                    Connected?.Invoke();
                    return;
                }

                Refused(result);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Todoist took it and the Keychain didn't. The token isn't anything to log.
            log.Error("The token couldn't be saved.", ex);
            Problem(Strings.TokenSaveFailed);
        }
        finally
        {
            Busy(false);
        }
    }

    private void Refused(TokenValidationResult result)
        => Problem(result == TokenValidationResult.Rejected ? Strings.TokenRejected : Strings.TokenUnreachable);

    private void Busy(bool busy)
    {
        _field.Enabled = !busy;
        _connect.Enabled = !busy && _field.StringValue.Trim().Length > 0;

        if (busy)
            _spinner.StartAnimation(this);
        else
            _spinner.StopAnimation(this);
    }

    private void Problem(string text)
    {
        _problem.StringValue = text;
        _problem.Hidden = false;
        View.Window?.MakeFirstResponder(_field);
    }

    private static NSStackView Stack(NSUserInterfaceLayoutOrientation orientation, nfloat spacing, params NSView[] views)
    {
        var stack = NSStackView.FromViews(views);
        stack.Orientation = orientation;
        stack.Spacing = spacing;
        return stack;
    }
}
