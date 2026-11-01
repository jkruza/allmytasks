# Research topics

Ideas that are interesting but not scheduled. They are written down so they are not lost and so they do not
quietly shape version 1 or 2. A topic becomes a design doc and a milestone only when a decision says so.
Each has a tracking issue for discussion.

## Runtime plug-ins

[#76](https://github.com/jkruza/allmytasks/issues/76). Today providers are compiled in
([providers.md](providers.md#registering-providers)), and third-party plug-ins are a stated non-goal until the
interfaces have survived several real providers. Jira, GitHub and GitLab in version 1 will test that.

Questions:

- In-process (a plug-in loaded with `AssemblyLoadContext`) or out-of-process (a separate executable speaking a
  small protocol over stdio)? In-process is easy but a plug-in then sees every credential and cannot be unloaded
  safely on all platforms. Out-of-process is safer and language-neutral, at the cost of a protocol to design and keep stable.
- What does a plug-in receive? Preference: only its own source settings and its own secrets, never the secret store.
- Distribution and trust: a folder the user copies into, signed packages, or nothing official at all?
- Does the model (`TaskItem`, `CalendarEvent`) become a public, versioned contract?

Exit criteria for the research: a one-page recommendation and a spike of a single provider running as a plug-in.

## MCP support

[#77](https://github.com/jkruza/allmytasks/issues/77). The Model Context Protocol lets an assistant call tools and
read data from a local program. The app already holds a clean, merged, read-only view of the user's work, which is
exactly what an assistant would want to ask about ("what is due this week?"), without needing each provider's
credentials.

Questions:

- **Server first**: expose read-only tools (upcoming, overdue, inbox, search) from the running app over a local
  transport only. A client role (the app calling other MCP servers as providers) is a separate, later question.
- Local-only transport and how a client is authorised, so another program on the machine cannot read the data silently.
- Explicit user consent, a visible on/off switch, and what is never exposed (secrets, raw provider payloads).
- Does it fit "read-only overview"? Yes as long as it stays read-only; write tools belong to version 2 rules.

## AI summaries and planning with local models

[#78](https://github.com/jkruza/allmytasks/issues/78). A daily or weekly summary ("three meetings, five overdue
tasks, two things in the Inbox") and suggestions for planning. Only models running on the user's machine, so that
"no backend, no telemetry" stays true; nothing leaves the computer.

Questions:

- Which local runtimes are practical to ship or to call (an installed Ollama, llama.cpp bindings, ONNX) and which
  model sizes work acceptably on an ordinary laptop without a GPU?
- What the model sees: titles and dates only, or notes too? Preference: titles, dates, sources and labels; notes
  only if the user opts in.
- Evaluation: how to check summaries are faithful (never invent a task), with a fixed fixture set of days.
- Opt-in, off by default, with the model's size and download made explicit.
- Planning that proposes dates is a form of write support; it only ever suggests, and applying a suggestion follows
  the [write rules](write-support.md#rules-that-apply-to-every-write).
