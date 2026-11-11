# Optional encryption of the local database

Tracked in [#144](https://github.com/jkruza/allmytasks/issues/144). **Status: proposed, version 2.**

## Goal

Cached events and tasks (titles, notes, attendees) sit in an SQLite file readable by anyone who can read the
user's profile folder. Credentials are already kept out of it ([storage-and-sync.md](storage-and-sync.md#credentials)).
The roadmap lists "Encrypt database at rest?" as open question 5 with the answer "not in version 1; document it".
This is the version 2 follow-up: an opt-in.

## What it protects against, and what it does not

Protects: a lost or stolen laptop without full-disk encryption, a copied profile folder, backups of the app data
folder. Does not protect: malware running as the user (it can ask the secret store for the key too), or the data
while the app is running.

Full-disk encryption (BitLocker, FileVault, LUKS) already covers most of this, so the feature is off by default
and the docs say to prefer disk encryption.

## Design

- **Opt-in** in Settings > Privacy: "Encrypt the local database".
- **Key** is random, 256 bit, created on enabling and stored only in the OS secret store, next to source
  credentials. On Linux without a keyring the existing PIN-derived fallback applies, with its documented weakness.
- **Engine** is the main open choice:

  | Option | For | Against |
  | --- | --- | --- |
  | SQLCipher via a `SQLitePCLRaw` bundle | Whole file encrypted, mature, per-page | Different native bundle on three OSes and in packaging; licence and size to check |
  | Field-level encryption of text columns | No native change | Search and filters cannot run in SQL; much more code |
  | Do nothing, rely on disk encryption | Zero cost | No protection for users without it |

  Proposal: SQLCipher, if the bundle builds on every platform in CI (CI builds all platforms every run).
- **Migration**: enabling copies the database into a new encrypted file and swaps it after a successful integrity
  check; the old file is overwritten and deleted. Disabling does the reverse. A failure leaves the original in place.
- **Key loss**: if the key is gone (secret store reset) the cache cannot be opened. That is acceptable because it is
  a cache: the app deletes the file and syncs again. Settings and source definitions are stored outside the
  encrypted file or are re-creatable; sources that lost their secrets already show "needs attention".

## Core changes

`IDatabaseKeyProvider` and an encrypted connection factory behind the existing storage interfaces. The migration
runner (numbered migrations, check numbering when parallel PRs add them) gets a "rekey" step that is not a normal
schema migration.

## Testing

Round-trip with a key, wrong key fails cleanly, migration plain to encrypted and back, interrupted migration keeps
the original, key-loss recovery. Packaging check on all three OSes.

## Open questions

- Licence and size of the SQLCipher bundle for an MIT project that ships installers.
- Is a per-launch password prompt wanted instead of the secret store? Proposal: no.
