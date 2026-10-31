using Foundation;
using Security;
using Termyn.Core.Platform;

namespace Termyn.Platform.Mac;

/// <summary>
/// Stores the API token in the user's login Keychain, as a password item Keychain Access shows
/// as "Termyn – Todoist API token". It's readable only by this user, and the token is never
/// written to a file.
/// </summary>
/// <remarks>
/// The item belongs to the login keychain rather than the data-protection one. That one needs a
/// signed app with a keychain access group, which a build from source isn't. The cost is that the
/// login keychain ties the item to the app that wrote it: a rebuilt app is a different app to it,
/// and asks for access, which "Always Allow" settles.
///
/// Once read, the token is kept in memory for the rest of the session. The engine asks for it
/// before every request, and every ask the Keychain hasn't been told "Always Allow" about is
/// another prompt — one a background sync would raise every few seconds.
/// </remarks>
public sealed class KeychainSecretStore : ISecretStore
{
    private const string Service = "Termyn";

    private const string Account = "todoist-token";

    /// <summary>
    /// The item's name, which is also what macOS quotes when it asks whether Termyn may read it:
    /// "Termyn wants to use your confidential information stored in …". The prompt's own words
    /// are the system's, so the name is the one part of it that can say what the access is for.
    /// </summary>
    private const string Label = "Termyn – Todoist API token";

    private const string Description = "Todoist API token";

    /// <summary>Whether this session has already made sure the item carries its name.</summary>
    private bool _named;

    private readonly Lock _gate = new();

    private string? _held;

    public string? GetToken() => ReadToken().Token;

    public TokenRead ReadToken()
    {
        lock (_gate)
        {
            if (_held is not null)
                return new(_held);

            var data = SecKeyChain.QueryAsData(Query(), wantPersistentReference: false, out var status);

            // Only "not found" is no token. Anything else — a locked Keychain, an access prompt
            // turned down or not yet answered — is a token out of reach for now, which the engine
            // waits out rather than reading as Todoist having turned the token away.
            if (status == SecStatusCode.ItemNotFound)
                return new(null);

            if (status != SecStatusCode.Success || data is null)
                return new(null, Unreachable: true);

            _held = NSString.FromData(data, NSStringEncoding.UTF8)?.ToString();
            if (_held is null)
                return new(null, Unreachable: true);

            Name();
            return new(_held);
        }
    }

    public void SetToken(string token)
    {
        lock (_gate)
        {
            var value = NSData.FromString(token, NSStringEncoding.UTF8);

            var status = SecKeyChain.Update(Query(), new SecRecord(SecKind.GenericPassword) { ValueData = value, Label = Label, Description = Description });
            if (status == SecStatusCode.ItemNotFound)
            {
                var item = Query();
                item.ValueData = value;
                item.Label = Label;
                item.Description = Description;
                status = SecKeyChain.Add(item);
            }

            if (status != SecStatusCode.Success)
                throw new InvalidOperationException($"The Keychain didn't store the token ({status}).");

            _held = token;
        }
    }

    public void ClearToken()
    {
        lock (_gate)
        {
            var status = SecKeyChain.Remove(Query());
            if (status is not (SecStatusCode.Success or SecStatusCode.ItemNotFound))
                throw new InvalidOperationException($"The Keychain didn't remove the token ({status}).");

            _held = null;
        }
    }

    /// <summary>
    /// Renames an item stored before it had its descriptive name, once a session. Asked about first
    /// rather than written every time: the name is an attribute, readable without the token, and an
    /// item already named needs nothing.
    /// </summary>
    /// <remarks>
    /// Best effort. An item that keeps its old name still holds the token, so nothing here is worth
    /// failing a read over.
    /// </remarks>
    private void Name()
    {
        if (_named)
            return;

        _named = true;
        if (SecKeyChain.QueryAsRecord(Query(), out var status) is { } found && status == SecStatusCode.Success && found.Label != Label)
            SecKeyChain.Update(Query(), new SecRecord(SecKind.GenericPassword) { Label = Label, Description = Description });
    }

    /// <summary>The item, by the two attributes that name it.</summary>
    private static SecRecord Query() => new(SecKind.GenericPassword) { Service = Service, Account = Account };
}
