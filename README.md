# All My Tasks

> Working title.

An open-source, cross-platform desktop app that brings your calendars and tasks
from many providers into one place. Built with .NET and [Avalonia](https://avaloniaui.net/),
it runs on Windows, macOS and Linux.

**Status:** early. Read-only providers so far: iCalendar feeds, CalDAV calendars and Todoist tasks
(add one with a Todoist API token). More providers are planned, see the [roadmap](docs/design/roadmap.md).

## Build and run

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
dotnet build AllMyTasks.slnx
dotnet test --solution AllMyTasks.slnx
dotnet run --project src/AllMyTasks
```

## Project layout

| Path | What it is |
| --- | --- |
| `src/AllMyTasks` | The Avalonia desktop app (views and view models) |
| `src/AllMyTasks.Core` | UI-independent logic: tasks, providers, aggregation |
| `src/AllMyTasks.Providers.ICal` | iCalendar feed and CalDAV providers |
| `src/AllMyTasks.Providers.Todoist` | Todoist task provider (API token, read-only) |
| `tests/` | xUnit tests for Core and each provider |

## Design

The intended design (data model, providers, views, storage, roadmap) is in
[docs/design](docs/design/README.md).

## Contributing

Contributions are welcome. Please read [CONTRIBUTING.md](CONTRIBUTING.md) first,
and report security issues as described in [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE)
