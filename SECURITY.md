# Security policy

## Reporting a vulnerability

Please **do not** open a public issue for security problems.

Report them privately through GitHub:
[Report a vulnerability](https://github.com/jkruza/allmytasks/security/advisories/new)
(Security tab → "Report a vulnerability").

Include what you found, how to reproduce it, and the version or commit you tested.
You should get a first response within 7 days. Once a fix is released, the advisory
will be published and you'll be credited unless you prefer otherwise.

## Supported versions

Only the latest release receives security fixes while the project is pre-1.0.

## Scope

All My Tasks stores credentials and tokens for the calendar and task providers you
connect. Issues that could expose those, or let a malicious calendar entry or task
run code or read local files, are in scope and especially welcome.
