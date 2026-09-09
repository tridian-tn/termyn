# Setting up signing

What has to exist before Termyn's artefacts can be signed, and why the choices are what they are.
The build side is already done — see [Signing](packaging.md#signing) — and takes a command line. This
is about producing one.

Everything here is from Microsoft's documentation as of August 2026, and the parts that move are the
prices and the eligibility rules. Check them rather than inheriting them from this page.

## The route, and why

**Azure Artifact Signing** — the service formerly called Trusted Signing; the docs renamed in 2026
but the Azure resource provider is still `Microsoft.CodeSigning`.

It's keyless, which matters because a publicly trusted signing key can no longer live in a file: the
CA/Browser Forum requires hardware or a cloud service, so a `.pfx` in a GitHub secret isn't an option
for a new certificate however many older guides describe it.

**Use SignTool with the dlib, not the GitHub Action.** There is an official
[`azure/artifact-signing-action`](https://github.com/azure/artifact-signing-action), and it signs
files you hand it — which is the problem. The uninstaller doesn't exist as a file anyone can hand it:
it's created and signed inside the installer compiler's own run, so it can only be signed by a
command that compiler invokes. One command line covers all three files; an action covers two.

## Eligibility, which decides who can do this at all

| | Where |
|---|---|
| **Individual** developers | United States or Canada only |
| **Organisations** | US, Canada, EU, UK, Australia, New Zealand, Japan, South Korea, Singapore, Switzerland, Norway, Israel |

So outside the US and Canada this has to be an organisation — a legal business entity, validated
against public records, with a business identifier, a website on a domain that matches, and a person
named on it who completes a photo-ID check.

Other things worth knowing before starting:

- **Free, trial and sponsored Azure subscriptions aren't accepted.** Pay-as-you-go or an enterprise
  agreement.
- **Identity validation takes 1 to 20 business days**, longer if more documents are asked for. It
  can't be expedited, and three failed document attempts ends the application.
- **Billing isn't pro-rated.** The full charge for the tier lands whenever in the month you start.
- **No EV certificates**, and no plan for them — so there's no buying past the reputation problem.
- Basic covers 5,000 signatures a month, Premium 100,000. Termyn signs three files per release.

## What to create

1. **Register the resource provider** `Microsoft.CodeSigning` on the subscription.
2. **Create an Artifact Signing account.** Basic is the right tier — 5,000 signatures a month against
   our three per release. Pick a region and remember which: the endpoint has to match it, and a
   mismatch shows up as a 403 rather than as anything mentioning regions. There's no UK region; North
   Europe (`https://neu.codesigning.azure.net`) and West Europe (`https://weu.codesigning.azure.net`)
   are the near ones.
3. **Complete identity validation** as an Organization, Public. This is the slow part.
4. **Create a certificate profile** of type Public Trust against that validated identity.
5. **Create a service principal** for CI and give it the **Artifact Signing Certificate Profile
   Signer** role. Signing fails with a 403 without it, and that role is separate from whatever rights
   created the account.

## What CI needs

Three secrets, from the service principal: `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`,
`AZURE_CLIENT_SECRET`. The account name, profile name and endpoint aren't secret and can be
repository variables.

The signing node needs, beyond the repository:

- **SignTool** from the Windows SDK, 10.0.22621 or newer — the 20348 SDK is explicitly unsupported by
  the dlib.
- **The .NET 8 runtime.** Not a typo: the dlib is built against 8, and Termyn's own .NET 10 doesn't
  satisfy it.
- **The dlib**, `Azure.CodeSigning.Dlib.dll`, from the `Microsoft.ArtifactSigning.Client` NuGet
  package — or the lot in one go with
  `winget install -e --id Microsoft.Azure.ArtifactSigningClientTools`.

A `metadata.json` naming the account, with the other credential types turned off so the runner
doesn't spend time trying each in turn before reaching the environment variables:

```json
{
  "Endpoint": "https://neu.codesigning.azure.net",
  "CodeSigningAccountName": "<account>",
  "CertificateProfileName": "<profile>",
  "ExcludeCredentials": [
    "ManagedIdentityCredential",
    "WorkloadIdentityCredential",
    "SharedTokenCacheCredential",
    "VisualStudioCredential",
    "VisualStudioCodeCredential",
    "AzureCliCredential",
    "AzurePowerShellCredential",
    "AzureDeveloperCliCredential",
    "InteractiveBrowserCredential"
  ]
}
```

Then the whole thing is one argument to the build:

```powershell
./packaging/build.ps1 -SkipTests -RequireInstaller -SignCommand @'
"<sdk>\x64\signtool.exe" sign /v /debug /fd SHA256 /tr "http://timestamp.acs.microsoft.com" /td SHA256 /dlib "<dlib>\x64\Azure.CodeSigning.Dlib.dll" /dmdf "<path>\metadata.json" $f
'@
```

`$f` is the file, substituted by the build script and by the installer compiler alike, so that one
string signs `Termyn.exe`, the installer and the uninstaller.

**Timestamp, and not as an afterthought.** Artifact Signing's certificates are valid for *three
days*. Without `/tr` every signature stops verifying almost immediately, rather than in a year or two
as with a conventional certificate. `http://timestamp.acs.microsoft.com` is the service's own.

## After it works

Signing is the beginning of SmartScreen reputation, not a way past it. Warnings can continue until a
given build has been downloaded enough times, and the counter is per file — each release starts its
own. Microsoft's advice for a build that keeps being flagged is to submit it through
[Microsoft Security Intelligence](https://www.microsoft.com/wdsi) for review.
