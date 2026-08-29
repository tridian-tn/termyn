# Packaging and release

Termyn ships two ways, both framework-dependent. Neither bundles the .NET runtime — that keeps the
download to a couple of megabytes and lets the runtime be patched on its own schedule.

## Building

```bash
pwsh ./packaging/build.ps1
```

Runs the tests, publishes, and writes both artefacts to `artifacts/`:

| Artefact | What it is |
|---|---|
| `Termyn-<version>-setup.exe` | The installer. Per-user, no elevation. |
| `Termyn-<version>-portable.zip` | Unzip and run. No shortcuts, no uninstaller. |

`-SkipTests` skips the suite when iterating on the packaging itself. Building the installer needs
Inno Setup 6 (`winget install JRSoftware.InnoSetup`); without it the script builds the zip and says
so rather than failing. `-RequireInstaller` turns that fallback into a failure, which is what you
want anywhere a release comes from: shipping half of what was asked for, announced in a warning
nobody reads, is worse than stopping.

**The version lives in one place** — `<Version>` in `Directory.Build.props`. It stamps the
executable, names both artefacts, and is what the update check compares against, so the installer,
the binary and the release tag cannot disagree. Release tags are that number with a leading `v`.

## Releasing

CI builds both artefacts on every pull request, with `-RequireInstaller`, and keeps them as a build
artefact. So the packaging is exercised by the same change that might break it, rather than
discovered to be broken later by whoever is trying to cut a release.

Pushing a `v*` tag does that and then attaches both files to the GitHub release for that tag,
creating it as a **draft** if it isn't there already. Draft rather than published because nothing is
signed yet: somebody should look before these are downloadable. Press publish to make the release
real, or delete the draft to abandon it.

The tag has to be the version that was built — `v1.2.0` against a `Directory.Build.props` still
saying `1.1.0` fails the job rather than attaching the previous version's installer to this
version's release. Bump `<Version>`, merge it, then tag.

What this doesn't do is tell you the installer *works*. A runner has the .NET Desktop Runtime
installed, so the missing-runtime path can't be exercised there; that one still wants a clean VM.

## Signing

Nothing published so far is signed, and Windows says so — SmartScreen's "Windows protected your PC"
on download, which in every other context means don't. The plumbing is here; the certificate isn't.

```bash
pwsh ./packaging/build.ps1 -SignCommand '"C:\path\signtool.exe" sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $f'
```

`$f` stands for the file, and is substituted the way Inno substitutes it — quoted — so one command
works for all three things that need signing:

| What | Signed by | Why it needs saying |
|---|---|---|
| `Termyn.exe` | the script, before packaging | So the zip carries a signed binary. The zip has no wrapper of its own, so this is the only signature its users ever see. |
| `Termyn-<version>-setup.exe` | Inno, via `SignTool` | The download itself. |
| `unins000.exe` | Inno, via `SignedUninstaller` | Written on the user's machine from a template, so signing the setup binary doesn't cover it. It's also the one they run later, on their own, when they've forgotten where the program came from. |

A command rather than a certificate on purpose: a publicly trusted signing key can't live in a file
any more, so every real option is a cloud service behind a signtool it ships. Naming the command
means changing service is a different string, not a different script.

Timestamp with RFC 3161 (`/tr`), or signatures stop verifying the day the certificate expires rather
than the day it was issued.

Without `-SignCommand` nothing is signed and the build is exactly as it was, so a local build needs
no certificate and no ceremony.

**What's checked.** After each file is signed the script asks whether a signature is actually on it,
and fails if not — a sign tool that succeeds while doing nothing is otherwise invisible until a user
meets the warning. It asks whether a signature is *present*, not whether this machine *trusts* it:
trust depends on the verifier's root store, and a build machine asking that question would fail on a
test certificate while proving nothing about whether the step ran.

Signing is not yet wired into CI, because that needs a certificate and nobody has one yet.
[Setting up signing](signing-setup.md) is what has to exist first, and why the choices are what they
are — including why the uninstaller rules out the obvious GitHub Action.

## What the installer does

Everything is per-user. It installs to `%LOCALAPPDATA%\Programs\Termyn`, writes only to `HKCU`, and
asks for no elevation — so it works on a managed machine without an administrator. That is enforced
rather than merely defaulted: `PrivilegesRequiredOverridesAllowed` is empty, so it can't be talked
into a machine-wide install from the command line either.

- **Runtime check.** Looks for the .NET 10 Desktop Runtime in the shared-framework directory, which
  is what the host actually loads — including the `dotnet\x64` path an Arm64 machine keeps the x64
  runtime under, since that's the one this build needs. Pre-release builds don't count: the host
  won't roll a release-versioned app onto one, so treating them as present would install cleanly and
  then fail to start. If the runtime is missing it offers to open the download page; declining
  installs anyway, with that said plainly. **Running silently it installs without prompting** and
  says so in the log — the prompt isn't suppressible, so asking would hang an unattended install.
- **Closes a running Termyn** before installing or uninstalling. It lives in the tray, so it usually
  is running, and files in use would otherwise force a reboot to finish.
- **Optional shortcuts** — Start menu always, desktop on request.
- **Optional launch at login**, written as `"<path>\Termyn.exe" --tray` — the same value and the same
  argument the app's own settings screen writes, so the two can't disagree about what it means. On
  its first run the app adopts whatever the Run key says rather than asserting its own default, which
  is what makes the installer's tick-box mean anything.

### Uninstall

Removes the program, the shortcuts and the Run entry — the last of these unconditionally, since the
app writes the same entry when you turn launch-at-login on from Settings and removal can't depend on
which of the two put it there. It then asks whether to remove your data:

```
%APPDATA%\Termyn        settings and the encrypted API token
%LOCALAPPDATA%\Termyn   the task cache, the outbox and the logs
```

Answering yes is a real logout — the token goes with it. **No** is the default button, because it is
the recoverable half of the choice. Nothing in the Todoist account is touched either way.

**Unattended, it keeps them.** Deleting someone's token and cache because they scripted an uninstall
is not a thing to do silently, and keeping them is the recoverable half of the choice. Say so
explicitly if that's what you want:

```bash
unins000.exe /VERYSILENT /REMOVEDATA=yes
```

`/REMOVEDATA=no` is also accepted, and the decision is written to the uninstall log either way.

## Updating

Manual for v1, as the spec has it. **Check for updates** in the tray menu or the command palette asks
GitHub for the latest release tag and, if it's newer than what's running, offers to open the release
page. Nothing downloads, nothing installs itself, and nothing about the account leaves the machine —
it is a GET for a version number, with no body, no query and no credentials. Not knowing (offline,
rate-limited, an unreadable answer) is reported as not knowing rather than as an error.

The link that comes back is only opened if it is an https URL on the project's own host. Opening goes
through `ShellExecute`, which runs a UNC path or a `file:` URL as readily as it opens a page — so
anyone able to tamper with the response would otherwise be one dialog away from launching a program
of their choosing. A release naming no page of its own falls back to the releases list.

Release tags are read as three numbers whatever they carry: `v2.0` and `v2.0.0.0` both mean `2.0.0`,
so a tag written either way compares and prints like any other.

To update, install over the top. The installer closes the running copy first, and your settings and
cache are untouched.

## Requirements

- Windows 10 1809 or later, x64
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)

.NET 10 is supported to November 2028, so there is no near-term forced bump.

## Icon assets

`assets/termyn.ico` is generated by `BrandIcon`, the same code that draws the tray icon — so the mark
on the executable, in the installer and in the tray is one design rather than three copies that
drift.

That claim is enforced: `BrandIconTests.The_committed_icon_is_the_one_the_code_draws_now` fails when
the committed file and the drawing disagree. Change the mark and that test tells you to regenerate:

```bash
pwsh ./packaging/build.ps1 -RegenerateIcon
```

which writes the sizes in `BrandIcon.IconSizes` and does nothing else.
