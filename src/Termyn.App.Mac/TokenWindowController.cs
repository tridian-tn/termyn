using CoreGraphics;
using Termyn.Core.Logging;
using Termyn.Presentation;

namespace Termyn.App.Mac;

/// <summary>
/// The window a first launch opens on: asks for the Todoist API token, checks it with Todoist and
/// keeps it in the Keychain.
/// </summary>
/// <remarks>
/// Drives <see cref="AuthPresenter"/> the way the Windows app's token form does. What goes wrong is
/// said under the field rather than in an alert, since the next thing to do is always to fix what's
/// in the field.
/// </remarks>
internal sealed class TokenWindowController : NSWindowController
{
    private const string IntegrationsUrl = "https://app.todoist.com/app/settings/integrations/developer";

    private readonly AuthPresenter _auth;
    private readonly ILog _log;
    private readonly Action _connected;

    private readonly NSSecureTextField _field;
    private readonly NSTextField _problem;
    private readonly NSButton _connect;
    private readonly NSProgressIndicator _spinner;

    public TokenWindowController(AuthPresenter auth, ILog log, Action connected)
        : base(new NSWindow(
            new CGRect(0, 0, 460, 200),
            NSWindowStyle.Titled | NSWindowStyle.Closable,
            NSBackingStore.Buffered,
            deferCreation: false))
    {
        _auth = auth;
        _log = log;
        _connected = connected;

        var window = Window;
        window.Title = Strings.TokenWindowTitle;

        var icon = NSImageView.FromImage(NSApplication.SharedApplication.ApplicationIconImage);
        icon.TranslatesAutoresizingMaskIntoConstraints = false;
        icon.WidthAnchor.ConstraintEqualTo(64).Active = true;
        icon.HeightAnchor.ConstraintEqualTo(64).Active = true;

        var heading = NSTextField.CreateLabel(Strings.TokenHeading);
        heading.Font = NSFont.BoldSystemFontOfSize(NSFont.SystemFontSize + 2)!;

        var explanation = NSTextField.CreateWrappingLabel(Strings.TokenExplanation);
        explanation.TextColor = NSColor.SecondaryLabel;
        explanation.PreferredMaxLayoutWidth = 340;

        var words = Stack(NSUserInterfaceLayoutOrientation.Vertical, 4, heading, explanation);
        words.Alignment = NSLayoutAttribute.Leading;

        var top = Stack(NSUserInterfaceLayoutOrientation.Horizontal, 16, icon, words);
        top.Alignment = NSLayoutAttribute.Top;

        _field = new NSSecureTextField { PlaceholderString = Strings.TokenPlaceholder };
        _field.Changed += (_, _) => _connect!.Enabled = _field.StringValue.Trim().Length > 0;

        _problem = NSTextField.CreateWrappingLabel(string.Empty);
        _problem.TextColor = NSColor.SystemRed;
        _problem.Font = NSFont.SystemFontOfSize(NSFont.SmallSystemFontSize)!;
        _problem.Hidden = true;

        var where = NSButton.CreateButton(Strings.TokenWhere, () => Open(IntegrationsUrl));
        where.Bordered = false;
        where.ContentTintColor = NSColor.Link;

        _spinner = new NSProgressIndicator
        {
            Style = NSProgressIndicatorStyle.Spinning,
            ControlSize = NSControlSize.Small,
            IsDisplayedWhenStopped = false,
        };

        // Return presses it, which is what a key equivalent of \r makes a button: the default one,
        // drawn in the accent colour.
        _connect = NSButton.CreateButton(Strings.TokenConnect, OnConnect);
        _connect.KeyEquivalent = "\r";
        _connect.Enabled = false;

        var buttons = Stack(NSUserInterfaceLayoutOrientation.Horizontal, 8, where, new NSView(), _spinner, _connect);
        buttons.Distribution = NSStackViewDistribution.Fill;

        var content = Stack(NSUserInterfaceLayoutOrientation.Vertical, 12, top, _field, _problem, buttons);
        content.Alignment = NSLayoutAttribute.Leading;
        content.EdgeInsets = new NSEdgeInsets(20, 20, 20, 20);
        foreach (var fill in new NSView[] { _field, _problem, buttons })
            fill.WidthAnchor.ConstraintEqualTo(content.WidthAnchor, 1, -40).Active = true;

        window.ContentView = content;
        content.WidthAnchor.ConstraintEqualTo(460).Active = true;
        window.Center();
        window.InitialFirstResponder = _field;
    }

    private async void OnConnect()
    {
        _problem.Hidden = true;
        Busy(true);

        try
        {
            switch (await _auth.ValidateAndStoreAsync(_field.StringValue))
            {
                case TokenValidationResult.Valid:
                    _connected();
                    return;

                case TokenValidationResult.Rejected:
                    Problem(Strings.TokenRejected);
                    break;

                default:
                    Problem(Strings.TokenUnreachable);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Todoist took it and the Keychain didn't. The token isn't anything to log.
            _log.Error("The token couldn't be saved.", ex);
            Problem(Strings.TokenSaveFailed);
        }
        finally
        {
            Busy(false);
        }
    }

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
        Window.MakeFirstResponder(_field);
    }

    private static void Open(string url) => NSWorkspace.SharedWorkspace.OpenUrl(new NSUrl(url));

    private static NSStackView Stack(NSUserInterfaceLayoutOrientation orientation, nfloat spacing, params NSView[] views)
    {
        var stack = NSStackView.FromViews(views);
        stack.Orientation = orientation;
        stack.Spacing = spacing;
        return stack;
    }
}
