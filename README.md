# All My Tasks

> Working title.

An open-source, cross-platform desktop app that brings your calendars and tasks
from many providers into one place. Built with .NET and [Avalonia](https://avaloniaui.net/),
it runs on Windows, macOS and Linux.

**Status:** early scaffold. The app shows sample tasks; no providers are connected yet.

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
| `tests/AllMyTasks.Core.Tests` | xUnit tests for Core |

## Contributing

Contributions are welcome. Please read [CONTRIBUTING.md](CONTRIBUTING.md) first,
and report security issues as described in [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE)
