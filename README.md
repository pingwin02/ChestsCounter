# ChestsCounter

Analyzes local Fortnite `.replay` files to count opened chests (solo, per-player, and team total), match counts, play time, daily averages, and Treasure Hunters mini-season reward probabilities, and generates an HTML chart report.

## Requirements

- .NET 10.0 SDK:

```cmd
winget install Microsoft.DotNet.SDK.10
```

## Usage

Run `run.cmd` or execute directly:

```cmd
dotnet build -c Release
bin\Release\net10.0\ChestsCounter.exe
```

Optional arguments:

- `--since YYYY-MM-DD` (default: `2026-11-01`)
- `--demos <path>` (default: `%LOCALAPPDATA%\FortniteGame\Saved\Demos`)

## Output

- Copies new/updated replays from the Demos folder into a local `replays/` archive so matches are preserved past Fortnite's 100-replay limit
- Live progress bar and summary in the terminal
- Interactive HTML chart report saved to `chests_report_YYYY-MM-DD_HH-mm-ss.html` next to the project
