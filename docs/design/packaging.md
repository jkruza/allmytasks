# Packaging and distribution

Tracked per operating system: Windows [#58](https://github.com/jkruza/allmytasks/issues/58),
macOS [#59](https://github.com/jkruza/allmytasks/issues/59), Linux [#60](https://github.com/jkruza/allmytasks/issues/60).

Today a `v*` tag builds self-contained single-file archives for six runtime ids and publishes a draft release
(`.github/workflows/release.yml`). Nothing is installable and nothing is signed, which makes alpha testing harder
than it needs to be and blocks features that need an installed app, such as
[notifications](reminders.md#platform-notifications) on Windows and macOS.

This document covers what is shared; the per-OS sections are the scope of the three issues.

**Status:** Windows (MSI) and Linux (AppImage, `.deb`, `.rpm`) are implemented ([#58](https://github.com/jkruza/allmytasks/issues/58),
[#60](https://github.com/jkruza/allmytasks/issues/60)), unsigned and not yet tried on a published release. macOS
([#59](https://github.com/jkruza/allmytasks/issues/59)) is not started. Build and run steps are under
[Building the packages locally](#building-the-packages-locally).

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
| Naming | `AllMyTasks-<version>-<os>-<arch>.<ext>`, for example `AllMyTasks-1.2.0-windows-x64.msi`; the existing archive names stay |
| Version | From the tag, as today (`-p:Version`); the same value goes into installer metadata |
| Integrity | `SHA256SUMS.txt` as today, plus GitHub build provenance attestations (`actions/attest-build-provenance`) for every release asset |
| CI | The `package` job in `ci.yml` builds every runtime id on pull requests and `main`, unsigned, as the existing rule requires: CI builds all platforms every run (macOS publishes and archives until #59 adds the bundle). Only the release workflow signs |
| Secrets | Signing material exists only as secrets of the protected `release` environment; never available to pull request workflows or forks |
| Where | `packaging/<os>/` holds installer definitions and scripts; workflow steps call them, so they can be run locally |
| Smoke test | Each package is installed (or unpacked) on its runner and the app is started once with `--version`, which prints `AllMyTasks <version>` and exits before any UI starts (a Windows GUI executable writes to stdout only when the output is redirected, which the smoke test does). Only x64 packages run on the x64 runners; the arm64 ones are built but not executed |
| Changes to `.github/` | The workflow changes need the repository's protected-file route (CODEOWNERS, maintainer approval) |

Both the Windows and the macOS pipelines need secrets that only the maintainer can create, so each issue starts with
an unsigned build and adds signing when the secrets exist.

## Windows

### Format

| Option | For | Against |
| --- | --- | --- |
| WiX MSI (per-user) | Installs without admin rights, works unsigned (with a SmartScreen warning), Start menu shortcut with an app user model id, scriptable (`msiexec /qn`) and usable with group policy, winget supports it | Authoring in XML; no automatic delete-data prompt |
| MSIX | Clean install/uninstall, toast identity built in | Cannot be installed at all without a trusted signature, so it is blocked until signing exists; strict packaging model, awkward with the registry-based launch at login |
| Inno Setup `.exe` | Small script, custom wizard pages | Not MSI |

**Decision (maintainer, 2026-10-31):** an MSI/MSIX installer is preferred. **MSI** with WiX v5 is built first because it
works today without a certificate; MSIX is a later option once signing is in place, and can be offered next to the MSI.
Inno Setup, the original proposal, is dropped.

The MSI (`win-x64` and `win-arm64`, plus the existing portable zip) installs per user to
`%LOCALAPPDATA%\Programs\AllMyTasks` and:

- creates a Start menu shortcut carrying the app user model id `com.jkruza.allmytasks` (the shortcut property
  `System.AppUserModel.ID`), which toast notifications are attributed to;
- offers, default off, a desktop shortcut and "start with Windows" as installer properties
  (`msiexec /i AllMyTasks.msi DESKTOPSHORTCUT=1 STARTWITHWINDOWS=1`), writing the same `HKCU` Run value as the app's own
  setting ([launch-at-login.md](launch-at-login.md)) with `--tray`;
- upgrades in place (a fixed upgrade code), refuses downgrades, and asks a running instance to close first;
- removes the shortcuts and the Run value it wrote on uninstall, and leaves the user data directory alone.

Known limits: a Run value written later by the app's own setting is not removed by the uninstaller (turn the setting
off first), and there is no "delete my data?" prompt, so data is always kept. The MSI product version is the numeric
`major.minor.patch` of the tag; prerelease suffixes only appear in the file name.

### Signing

SmartScreen warns on unsigned installers until reputation builds, and a signed installer builds it faster. The release
workflow has a "Windows signing (not configured)" step that only reports the build as unsigned; the signing steps for the
executable and the MSI are added there once the route below is chosen (credentials live in the `release` environment, and
the step must skip cleanly when they are absent).

| Option | Notes |
| --- | --- |
| SignPath Foundation (free for qualifying open-source projects) | Needs an application and the project to meet their conditions; the release job submits the artifact for signing |
| Azure Trusted Signing | Paid; short-lived certificates, simple CI integration |
| Own certificate | OV/EV certificate purchase; secrets in the `release` environment |

**Proposal:** build unsigned first; apply to SignPath Foundation, with a Certum open-source code-signing certificate as
the fallback. Azure Trusted Signing (now Artifact Signing) is likely not available to an individual developer in Poland.
This is the maintainer's call and the issue lists it as the first thing to decide.

### winget

Once signed releases exist, a manifest in `microsoft/winget-pkgs` (installer type `wix`/`msi`, per-user scope). A
`wingetcreate` step in the release workflow can produce the pull request; the first submission is manual.

### Windows checks

- `packaging/windows/smoke.ps1` (x64 runner): silent per-user install with `STARTWITHWINDOWS=1`, executable, shortcut and
  Run value present, `--version` correct, uninstall removes all three. arm64 is built, not run.
- Not checked automatically: the shortcut's app user model id (needed by [reminders](reminders.md)); inspect it once on a
  real machine.

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
| AppImage | `appimagetool` over an AppDir (binary, `.desktop`, icon) | Any distribution; no install. The arm64 image is assembled on the x64 runner with the pinned arm64 runtime (no arm64 runner needed) and is not executed in CI |
| `.deb` | nfpm | Debian, Ubuntu, Mint |
| `.rpm` | nfpm (same config) | Fedora, openSUSE |
| tarball | as today | Portable |

Tool versions (nfpm, appimagetool) are pinned in `packaging/linux/build.sh` with their SHA-256. The arm64 AppImage
runtime is downloaded from the type2-runtime `continuous` release, so its hash can change upstream; the build then fails
until the pin is updated, which is intended.

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

`packaging/linux/smoke.sh` (x64 runner, needs Docker) does all of these:

- Installs the `.deb` in clean Debian and Ubuntu containers and the `.rpm` in a Fedora container, runs `--version`, and
  checks that uninstall leaves the user's data.
- Runs the AppImage with `--appimage-extract-and-run`, since FUSE 2 is often missing.
- Checks that the `.desktop` file passes `desktop-file-validate`.

The dependency lists in `packaging/linux/nfpm.yaml` (ICU, OpenSSL, fontconfig, X11 libraries) are a first version; the
container installs are what confirms them, and the list is adjusted to whatever those runs report. The desktop file's
`StartupWMClass=AllMyTasks` is unverified on real desktops.

## Building the packages locally

```sh
# Linux (needs the .NET SDK, curl, tar; Docker for the smoke test)
bash packaging/publish.sh linux-x64 1.2.0 out/linux-x64
bash packaging/linux/build.sh linux-x64 1.2.0 out/linux-x64 dist      # AppImage, deb, rpm in dist/
bash packaging/linux/smoke.sh 1.2.0 dist

# Windows (PowerShell; installs the pinned WiX tool)
bash packaging/publish.sh win-x64 1.2.0 out/win-x64
pwsh packaging/windows/build.ps1 -Rid win-x64 -Version 1.2.0 -PublishDir out/win-x64 -DistDir dist
pwsh packaging/windows/smoke.ps1 -Msi dist/AllMyTasks-1.2.0-windows-x64.msi -Version 1.2.0
```

`scripts/make-icons.py` also writes the hicolor PNGs in `packaging/linux/icons/`.

## Tests and CI cost

Packaging jobs are the most expensive CI part (installer tools, notarization). On pull requests the unsigned build runs
on the same path filter as the build job: docs-only changes skip it ([ci-docs-only-changes.md](ci-docs-only-changes.md)).
Signing and notarization run only in the release workflow.

## Order of work

1. Shared: `--version` flag, `packaging/` layout, attestations and naming (done, with 2 and 3).
2. Linux (no cost, no external accounts): AppImage, `.deb`, `.rpm` (done).
3. Windows unsigned MSI (done); then decision and setup for signing.
4. macOS unsigned bundle and dmg; then signing once the Apple account exists.
5. winget manifest after the first signed Windows release.

## Open questions

| # | Question | Proposal |
| --- | --- | --- |
| 1 | Windows signing route | Apply to SignPath Foundation, Trusted Signing as fallback |
| 2 | Pay for an Apple Developer account? | Maintainer's decision; unsigned dmg until then |
| 3 | Universal macOS build or two dmgs? | Two dmgs first |
| 4 | MSIX as well as the MSI? | Later, once signing exists (maintainer prefers MSI/MSIX; MSI first) |
| 5 | Project-owned apt/dnf repository? | No |
