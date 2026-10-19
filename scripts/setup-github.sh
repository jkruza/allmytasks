#!/usr/bin/env bash
# Creates the public GitHub repo for All My Tasks and applies its security settings.
#
# Run once from the repository root, after `git init -b main` and a first commit:
#   gh auth login
#   ./scripts/setup-github.sh
#
# Safe to re-run: each step reports OK or FAILED on its own, and failed steps can
# be redone by hand (see docs/MAINTAINING.md). GitHub's API changes from time to
# time, so check the result in Settings afterwards.
set -uo pipefail

OWNER="${OWNER:-jkruza}"
REPO="${REPO:-allmytasks}"
FULL="$OWNER/$REPO"
DESCRIPTION="Open-source, cross-platform desktop app that brings calendars and tasks from many providers into one place."

failed=()

step() {
  local title="$1"; shift
  printf '%-60s' "$title"
  local output
  if output="$("$@" 2>&1)"; then
    echo "OK"
  else
    echo "FAILED"
    printf '%s\n' "$output" | sed 's/^/    /'
    failed+=("$title")
  fi
}

api() { gh api -H "Accept: application/vnd.github+json" -H "X-GitHub-Api-Version: 2022-11-28" "$@"; }

command -v gh >/dev/null || { echo "Install the GitHub CLI first: https://cli.github.com"; exit 1; }
gh auth status >/dev/null 2>&1 || { echo "Run 'gh auth login' first."; exit 1; }
git rev-parse --verify HEAD >/dev/null 2>&1 || { echo "Commit the scaffold first (git init -b main && git add -A && git commit -s)."; exit 1; }

USER_ID="$(api user --jq .id)"

# --- Repository -----------------------------------------------------------------

create_repo() {
  if gh repo view "$FULL" >/dev/null 2>&1; then
    echo "already exists"
    git remote get-url origin >/dev/null 2>&1 || git remote add origin "https://github.com/$FULL.git"
    git push -u origin main
  else
    gh repo create "$FULL" --public --description "$DESCRIPTION" --source . --remote origin --push
  fi
}
step "Create public repo $FULL and push main" create_repo

step "Repo options (squash/rebase only, delete merged branches)" \
  api -X PATCH "repos/$FULL" \
    -F has_wiki=false -F has_projects=false \
    -F allow_merge_commit=false -F allow_squash_merge=true -F allow_rebase_merge=true \
    -F allow_auto_merge=false -F delete_branch_on_merge=true \
    -F web_commit_signoff_required=true

# --- Security features --------------------------------------------------------------

step "Secret scanning and push protection" \
  api -X PATCH "repos/$FULL" --input - <<'JSON'
{"security_and_analysis":{"secret_scanning":{"status":"enabled"},"secret_scanning_push_protection":{"status":"enabled"}}}
JSON

step "Dependabot alerts"           api -X PUT "repos/$FULL/vulnerability-alerts"
step "Dependabot security updates" api -X PUT "repos/$FULL/automated-security-fixes"
step "Private vulnerability reporting" api -X PUT "repos/$FULL/private-vulnerability-reporting"

# --- Actions ------------------------------------------------------------------------

step "Actions: GitHub-owned actions only, SHA pinning required" \
  api -X PUT "repos/$FULL/actions/permissions" --input - <<'JSON'
{"enabled":true,"allowed_actions":"selected","sha_pinning_required":true}
JSON

step "Actions: no Marketplace or third-party actions" \
  api -X PUT "repos/$FULL/actions/permissions/selected-actions" --input - <<'JSON'
{"github_owned_allowed":true,"verified_allowed":false,"patterns_allowed":[]}
JSON

step "Actions: read-only GITHUB_TOKEN, can't approve PRs" \
  api -X PUT "repos/$FULL/actions/permissions/workflow" --input - <<'JSON'
{"default_workflow_permissions":"read","can_approve_pull_request_reviews":false}
JSON

step "Actions: fork PRs from all outside contributors need approval" \
  api -X PUT "repos/$FULL/actions/permissions/fork-pr-contributor-approval" --input - <<'JSON'
{"approval_policy":"all_external_contributors"}
JSON

# --- Release environment ----------------------------------------------------------------

step "Environment 'release' with you as required reviewer" \
  api -X PUT "repos/$FULL/environments/release" --input - <<JSON
{"wait_timer":0,"prevent_self_review":false,
 "reviewers":[{"type":"User","id":$USER_ID}],
 "deployment_branch_policy":{"protected_branches":false,"custom_branch_policies":true}}
JSON

add_tag_policy() {
  if api "repos/$FULL/environments/release/deployment-branch-policies" \
       --jq '.branch_policies[] | select(.type=="tag" and .name=="v*") | .id' | grep -q .; then
    echo "already exists"
  else
    api -X POST "repos/$FULL/environments/release/deployment-branch-policies" -f name='v*' -f type=tag
  fi
}
step "Environment 'release': only v* tags may deploy" add_tag_policy

# --- Rulesets -----------------------------------------------------------------------

apply_ruleset() {
  local file="$1" name id
  name="$(jq -r .name "$file")"
  id="$(api "repos/$FULL/rulesets" --jq ".[] | select(.name==\"$name\") | .id")"
  if [[ -n "$id" ]]; then
    api -X PUT "repos/$FULL/rulesets/$id" --input "$file"
  else
    api -X POST "repos/$FULL/rulesets" --input "$file"
  fi
}
command -v jq >/dev/null || echo "jq is needed for the ruleset steps."
step "Ruleset: protect main"         apply_ruleset .github/rulesets/main-protection.json
step "Ruleset: protect release tags" apply_ruleset .github/rulesets/protect-release-tags.json

# --- Summary ---------------------------------------------------------------------------

echo
if ((${#failed[@]})); then
  echo "Some steps failed. Redo them by hand (see docs/MAINTAINING.md):"
  printf '  - %s\n' "${failed[@]}"
else
  echo "All steps succeeded."
fi
cat <<'TXT'

Still to do by hand:
  - Use a passkey or security key for your GitHub account (Settings > Password and authentication).
  - Check https://github.com/jkruza/allmytasks/settings against docs/MAINTAINING.md.
TXT
((${#failed[@]} == 0))
