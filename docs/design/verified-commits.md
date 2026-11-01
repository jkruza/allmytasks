# Verified commits

Issue: [#90](https://github.com/jkruza/allmytasks/issues/90).

## Problem

Commits pushed with plain `git` from a Claude cloud container show "Unverified" on GitHub,
because the container has no signing key. Commits GitHub creates itself (web edits, squash merges) are
signed by GitHub and show Verified.

## Decisions

1. **Require signatures on `main`.** `main-protection` gets a `required_signatures` rule.
   The rule applies to commits that land on `main`, not to commits on PR branches.
2. **Squash merge only.** The squash commit is created and signed by GitHub (web-flow key),
   so it is Verified regardless of how the branch commits were made. Rebase merges rewrite
   commits without a valid signature, so they are disabled in the ruleset and in the
   repo options (`scripts/setup-github.sh`).
3. **Claude's branch commits stay unsigned.** Tested on this change: a commit made with the
   GitHub MCP `create_or_update_file` tool (Contents API, bot account `jkruza-ai-bot`) came back
   `"verified": false, "reason": "unsigned"`. So API commits are not a way to get Verified branch
   commits with the current tooling. This does not matter for `main`, because PR branches are
   squashed. No signing key is placed in the cloud, and nothing there can commit as Jarek.
4. **Jarek signs with SSH on his machine** and registers the public key as a Signing key.

## Checks

- **DCO.** Sign-off is a `Signed-off-by:` line in the message, independent of the signature, so
  it keeps working unchanged. Web-commit sign-off is also required in the
  repo settings.
- **Identity.** Claude's commits are authored by the bot account (`jkruza-ai-bot`) or, with
  plain `git`, by the noreply identity. Neither matters for `main`: the squash commit takes the
  PR author and is committed by GitHub.
- **Squash merges.** Unaffected: GitHub signs the merge.
- **Dependabot.** Its commits are signed by GitHub, and it uses PRs, so it merges as usual.
- **Admin bypass.** Unchanged (bypass through a PR only).

## Rollout

Jarek applies the ruleset (the bot is not an admin): run the ruleset steps of
`scripts/setup-github.sh` with an admin token, or import `.github/rulesets/main-protection.json`
under Settings → Rules → Rulesets. Also switch off "Allow rebase merging" in Settings → General.

## Open option

If Verified branch commits ever matter, give the bot account its own SSH or GPG signing key
(registered on `jkruza-ai-bot`, never Jarek's key). The private key would have to live in the
cloud environment as a secret, so this is a deliberate trade-off and is not done here.
