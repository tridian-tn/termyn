using Foundation;
using Security;
using Termyn.Core.Platform;

namespace Termyn.Platform.Mac;

/// <summary>
/// Stores the API token in the user's login Keychain, as a password item Keychain Access shows
/// as "Termyn". It's readable only by this user, and the token is never written to a file.
/// </summary>
/// <remarks>
/// The item belongs to the login keychain rather than the data-protection one. That one needs a
/// signed app with a keychain access group, which a build from source isn't. The cost is that the
/// login keychain ties the item to the app that wrote it: a rebuilt app is a different app to it,
/// and asks once for access, which "Always Allow" settles.
/// </remarks>
public sealed class KeychainSecretStore : ISecretStore
{
    private const string Service = "Termyn";

    private const string Account = "todoist-token";

    public string? GetToken()
    {
        var data = SecKeyChain.QueryAsData(Query(), wantPersistentReference: false, out var status);

        // Anything but the token itself is no token for now, including a Keychain that's locked or
        // an access prompt the user turned down. The engine reads a null as "nothing to send with",
        // never as Todoist having turned the token away, so the account isn't thrown out over it.
        return status == SecStatusCode.Success && data is not null
            ? NSString.FromData(data, NSStringEncoding.UTF8)?.ToString()
            : null;
    }

    public void SetToken(string token)
    {
        var value = NSData.FromString(token, NSStringEncoding.UTF8);

        var status = SecKeyChain.Update(Query(), new SecRecord(SecKind.GenericPassword) { ValueData = value });
        if (status == SecStatusCode.ItemNotFound)
        {
            var item = Query();
            item.ValueData = value;
            item.Label = Service;
            item.Description = "Todoist API token";
            status = SecKeyChain.Add(item);
        }

        if (status != SecStatusCode.Success)
            throw new InvalidOperationException($"The Keychain didn't store the token ({status}).");
    }

    public void ClearToken()
    {
        var status = SecKeyChain.Remove(Query());
        if (status is not (SecStatusCode.Success or SecStatusCode.ItemNotFound))
            throw new InvalidOperationException($"The Keychain didn't remove the token ({status}).");
    }

    /// <summary>The item, by the two attributes that name it.</summary>
    private static SecRecord Query() => new(SecKind.GenericPassword) { Service = Service, Account = Account };
}
