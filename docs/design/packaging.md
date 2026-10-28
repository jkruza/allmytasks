# Packaging and distribution

Tracked per operating system: Windows [#58](https://github.com/jkruza/allmytasks/issues/58),
macOS [#59](https://github.com/jkruza/allmytasks/issues/59), Linux [#60](https://github.com/jkruza/allmytasks/issues/60).

Today a `v*` tag builds self-contained single-file archives for six runtime ids and publishes a draft release
(`.github/workflows/release.yml`). Nothing is installable and nothing is signed, which makes alpha testing harder
than it needs to be and blocks features that need an installed app, such as
[notifications](reminders.md#platform-notifications) on Windows and macOS.

This document covers what is shared; the per-OS sections are the scope of the three issues.

## Today

| RID | Output |
| --- | --- |
| `win-x64`, `win-arm64` | `AllMyTasks-<tag>-<rid>.zip` |
| `linux-x64`, `linux-arm64` | `AllMyTasks-<tag>-<rid>.tar.gz` |
| `osx-x64`, `osx-arm64` | `AllMyTasks-<tag>-<rid>.tar.gz` |

Plus `SHA256SUMS.txt`, a draft release behind the `release` environment approval. The archives stay as the
portable option on every OS.

## Goals

- One installable, per-user artifact per OS and architecture, built in CI from the tag.
- Installers register what the app needs from the OS: shortcut and notification identity (Windows), bundle identity
  (macOS), desktop entry and icons (Linux).
- Signed where signing is available, with an honest unsigned path that is documented.
- Every pull request builds every platform's package (unsigned), so packaging cannot rot until release day.

## Non-goals

- A self-updater inside the app. A "new version available" notice that opens the releases page is a possible later
  feature, not part of these issues.
- Store distribution (Microsoft Store, Mac App Store, Snap). Flatpak is a Linux follow-up.
- System-wide (all users) installs.

## Shared decisions

| Topic | Decision |
| --- | --- |
| Naming | `AllMyTasks-<version>-<os>-<arch>.<ext>`, for example `AllMyTasks-1.2.0-windows-x64-setup.exe`; the existing archive names stay |
| Version | From the tag, as today (`-p:Version`); the same value goes into installer metadata |
| Integrity | `SHA256SUMS.txt` as today, plus GitHub build provenance attestations (`actions/attest-build-provenance`) for every release asset |
| CI | The same packaging jobs run on pull requests and `main` on all three OS runners, unsigned, as the existing rule requires: CI builds all platforms every run. Only the release workflow signs |
| Secrets | Signing material exists only as secrets of the protected `release` environment; never available to pull request workflows or forks |
| Where | `packaging/<os>/` holds installer definitions and scripts; workflow steps call them, so they can be run locally |
| Smoke test | Each package is installed (or unpacked) on its runner and the app is started once with a `--version` style flag that exits; this flag does not exist yet and is part of the first implementation PR |
| Changes to `.github/` | The workflow changes need the repository's protected-file route (CODEOWNERS, maintainer approval) |

Both the Windows and the macOS pipelines need secrets that only the maintainer can create, so each issue starts with
an unsigned build and adds signing when the secrets exist.

## Windows

### Format

| Option | For | Against |
| --- | --- | --- |
| Inno Setup `.exe` | Per-user install without admin, shortcut with app user model id, uninstaller hooks, small script, runs on the Windows runner | Not MSI, so no group policy deployment |
| MSIX | Clean install/uninstall, toast identity built in | Needs a certificate to install at all, strict packaging model, awkward with the registry-based launch at login |
| WiX MSI | Enterprise friendly | More work, not needed for a personal-use app |

**Proposal:** Inno Setup, per-user (`%LOCALAPPDATA%\Programs\AllMyTasks`), for `win-x64` and `win-arm64`, plus the
existing portable zip.

The installer:

- creates a Start menu shortcut carrying the app user model id used by toast notifications;
- offers (default off) a desktop shortcut and "start with Windows", writing the same `HKCU` Run value the app writes
  itself ([launch-at-login.md](launch-at-login.md));
- on uninstall removes that Run value and the shortcut, and asks whether to delete the user data directory (default no);
- upgrades in place and closes a running instance first.

### Signing

SmartScreen warns on unsigned installers until reputation builds, and a signed installer builds it faster.

| Option | Notes |
| --- | --- |
| SignPath Foundation (free for qualifying open-source projects) | Needs an application and the project to meet their conditions; the release job submits the artifact for signing |
| Azure Trusted Signing | Paid; short-lived certificates, simple CI integration |
| Own certificate | OV/EV certificate purchase; secrets in the `release` environment |

**Proposal:** build unsigned first; apply to SignPath Foundation; fall back to Trusted Signing if refused. This is the
maintainer's call and the issue lists it as the first thing to decide.

### winget

Once signed releases exist, a manifest in `microsoft/winget-pkgs` (installer type `inno`, per-user scope). A
`wingetcreate` step in the release workflow can produce the pull request; the first submission is manual.

### Windows checks

- Install, start, launch at login on, uninstall removes the Run value, on x64 and arm64 runners where available.
- The shortcut has the expected app user model id (needed by [reminders](reminders.md)).

## macOS

### Bundle

`AllMyTasks.app` for `osx-arm64` and `osx-x64`:

- `Contents/MacOS/AllMyTasks` is the published single-file binary; `Info.plist` with `CFBundleIdentifier`
  `com.jkruza.allmytasks` (the same id as the launchd label in [launch-at-login.md](launch-at-login.md)),
  `CFBundleShortVersionString` from the tag, `LSMinimumSystemVersion`, `NSHighResolutionCapable`.
- `Contents/Resources/AllMyTasks.icns` generated from the existing icon sources
  ([app-icon.md](app-icon.md)) by the same script that produces the other icon files.
- The launch-at-login plist already uses the running executable path, which points into the bundle.
- Whether the tray-only start should hide the Dock icon (`LSUIElement`) is decided in the implementation PR together
  with the tray behaviour on macOS.

A **universal** binary (`lipo` of the two builds) is optional: one download instead of two, at the cost of a larger file
and an extra CI step. **Proposal:** ship the two per-arch dmgs first.

### Image

A `.dmg` with the app and an Applications link, built with `hdiutil` (or `create-dmg`) on the macOS runner. Downloaded
`.tar.gz` of the bundle stays as the portable option.

### Signing and notarization

| Level | What the user sees | Needs |
| --- | --- | --- |
| Unsigned / ad-hoc (`codesign -s -`) | Gatekeeper blocks it; user must right-click Open or clear the quarantine flag; the keychain may prompt more often | nothing |
| Developer ID signed + notarized + stapled | Opens normally | Apple Developer Program membership (paid, yearly), certificate, notarytool API key |

Signing needs the hardened runtime with entitlements a .NET app requires (allow JIT, allow unsigned executable memory,
and disabling library validation if native libraries are extracted at startup). Whether the single-file extraction
(`IncludeNativeLibrariesForSelfExtract`) can be avoided on macOS to keep the entitlements minimal is checked in the
implementation PR.

**Proposal:** produce unsigned bundles and dmgs first; sign and notarize when the maintainer has an Apple developer
account. Secrets (base64 certificate, its password, API key id/issuer/key) go into the `release` environment, and the
job imports the certificate into a temporary keychain that is deleted at the end.

A signed, stable bundle id also matters for correct keychain access (secret store) and for
[notifications](reminders.md#platform-notifications).

### macOS checks

- `codesign --verify --deep --strict`, `spctl --assess` and `xcrun stapler validate` for signed builds.
- Start from the dmg-installed bundle, enable launch at login, confirm the launchd entry targets the bundle.

## Linux

### Formats

| Format | How built | For |
| --- | --- | --- |
| AppImage | `appimagetool` over an AppDir (binary, `.desktop`, icon) | Any distribution; no install; arm64 built on an arm64 runner |
| `.deb` | nfpm | Debian, Ubuntu, Mint |
| `.rpm` | nfpm (same config) | Fedora, openSUSE |
| tarball | as today | Portable |

The launch-at-login code already registers the `APPIMAGE` path when set, and a `.deb`/`.rpm` install registers the
installed binary.

Installed layout for `.deb`/`.rpm`: binary in `/opt/allmytasks/` with a symlink in `/usr/bin`,
`/usr/share/applications/com.jkruza.allmytasks.desktop` (category Office; Calendar; ProjectManagement),
icons under `/usr/share/icons/hicolor/<size>/apps/`. The app's `.desktop` file is the one autostart copies.

Both package formats need the .NET runtime's native dependencies (ICU, OpenSSL, fontconfig and similar libraries) as
package dependencies, or the self-contained build must not depend on them being absent; the exact list is established
by installing on a clean container for each target in CI.

### Not in these issues

- **Flatpak / Flathub:** the sandbox changes how autostart (background portal), the tray (StatusNotifier permission) and
  the keyring (secret portal) work, and launch-at-login is documented as excluded from sandboxes. Worth doing, but as its
  own design and issue.
- Snap, distribution repositories (apt/dnf) and AUR packages by third parties.

### Signing

`.deb` and `.rpm` repositories sign their metadata; single files do not need it. Integrity comes from the shared
checksums and attestations. A GPG-signed apt/dnf repository is out of scope.

### Linux checks

- Install the `.deb` in clean Debian and Ubuntu containers and the `.rpm` in a Fedora container; run the version smoke
  test; uninstall leaves the user's data.
- Run the AppImage on a container with FUSE unavailable (`--appimage-extract-and-run`), since FUSE 2 is often missing.
- The `.desktop` file passes `desktop-file-validate`.

## Tests and CI cost

Packaging jobs are the most expensive CI part (installer tools, notarization). On pull requests the unsigned build runs
on the same path filter as the build job: docs-only changes skip it ([ci-docs-only-changes.md](ci-docs-only-changes.md)).
Signing and notarization run only in the release workflow.

## Order of work

1. Shared: `--version` flag, `packaging/` layout, attestations and naming (small PR, touches `.github/`).
2. Linux (no cost, no external accounts): AppImage, `.deb`, `.rpm`.
3. Windows unsigned installer; then decision and setup for signing.
4. macOS unsigned bundle and dmg; then signing once the Apple account exists.
5. winget manifest after the first signed Windows release.

## Open questions

| # | Question | Proposal |
| --- | --- | --- |
| 1 | Windows signing route | Apply to SignPath Foundation, Trusted Signing as fallback |
| 2 | Pay for an Apple Developer account? | Maintainer's decision; unsigned dmg until then |
| 3 | Universal macOS build or two dmgs? | Two dmgs first |
| 4 | MSIX later for the Microsoft Store? | Not now |
| 5 | Project-owned apt/dnf repository? | No |
