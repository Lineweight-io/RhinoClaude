# RhinoClaude — Claude AI Plugin for Rhinoceros 3D

A plugin that integrates Claude (by Anthropic) directly into Rhino, enabling you to ask questions, generate scripts, and get AI assistance with your 3D modeling workflow.

## Features (Phase 1)

- **ClaudeAsk** — Ask Claude questions from the Rhino command line, with optional scene context
- **ClaudeRunScript** — Describe a task in plain English, Claude generates a Python script, and you can run it directly
- **ClaudeSetKey** — Securely store your Anthropic API key in Rhino's plugin settings
- **Scene Context** — Automatically sends information about your layers, objects, and selections to Claude
- **Conversation History** — Multi-turn conversations that maintain context across queries

## Requirements

- **Rhino 7** (Windows, .NET Framework 4.8) and/or **Rhino 8** (Windows, .NET 7)
- **Visual Studio 2022** (Community edition is fine)
- **.NET SDK** (for building)
- **Anthropic API Key** — get one at [console.anthropic.com](https://console.anthropic.com)

## Project Structure

```
RhinoClaude/
├── RhinoClaude.sln              # Visual Studio solution
└── RhinoClaude/
    ├── RhinoClaude.csproj       # Project file (multi-targets net48 + net7.0)
    ├── RhinoClaudePlugin.cs     # Main plugin entry point
    ├── Properties/
    │   └── AssemblyInfo.cs      # Assembly metadata
    ├── Commands/
    │   ├── ClaudeAskCommand.cs       # Ask Claude questions
    │   ├── ClaudeSetKeyCommand.cs    # Configure API key
    │   └── ClaudeRunScriptCommand.cs # Generate & run scripts
    └── Services/
        ├── ClaudeApiService.cs       # Anthropic API client
        └── SceneContextCollector.cs  # Rhino scene → text context
```

## Building

1. **Clone or copy** this folder to your development machine.

2. **Open** `RhinoClaude.sln` in Visual Studio 2022.

3. **Restore NuGet packages** (should happen automatically):
   - `RhinoCommon` — the Rhino SDK (compile-only, provided at runtime by Rhino)
   - `System.Text.Json` — JSON serialization (for .NET Framework 4.8 target)

4. **Build** the solution:
   - For Rhino 7: build the `net48` target
   - For Rhino 8: build the `net7.0-windows` target

5. **Output**: The compiled plugin will be at:
   ```
   bin/Debug/net48/RhinoClaude.rhp        (Rhino 7)
   bin/Debug/net7.0-windows/RhinoClaude.rhp  (Rhino 8)
   ```

## Installation

### Manual Install
1. Build the project for your Rhino version.
2. In Rhino, run `PlugInManager`.
3. Click "Install" and browse to the `.rhp` file.
4. Restart Rhino.

### Drag & Drop
Simply drag the `.rhp` file into an open Rhino window.

## Setup

1. **Set your API key** (only needed once — it's saved persistently):
   ```
   ClaudeSetKey
   ```
   Enter your Anthropic API key when prompted (starts with `sk-ant-`).

   Alternatively, set the `ANTHROPIC_API_KEY` environment variable.

## Usage

### Ask Claude a Question
```
ClaudeAsk
```
Type your question when prompted. Options:
- **IncludeSceneContext** (Yes/No) — sends layer/object info to Claude
- **ObjectScope** (All/SelectedOnly) — what objects to include in context
- **ConversationHistory** (Keep/Clear) — maintain multi-turn conversation

**Example:**
```
Command: ClaudeAsk
Ask Claude: How do I create a lofted surface between these curves?
```

### Generate and Run a Script
```
ClaudeRunScript
```
Describe what you want in plain English. Claude generates a Python script,
shows it to you for review, and asks for confirmation before running it.

**Example:**
```
Command: ClaudeRunScript
Describe what you want the script to do: Create a 10x10 grid of spheres with random radii between 0.5 and 2.0
```

Claude will generate the script, display it, and ask `Run / Cancel`.

## How It Works

1. **Scene Context**: When enabled, the plugin reads your Rhino document — layers, object types, selected geometry details (dimensions, curve lengths, face counts, etc.) — and sends that as context with your prompt.

2. **Claude API**: Uses the Anthropic Messages API directly via HTTP (no SDK dependency). The system prompt tells Claude it's inside Rhino and should generate RhinoPython scripts.

3. **Script Execution**: Generated Python scripts run through Rhino's built-in `PythonScript` engine, the same one that powers the `EditPythonScript` editor.

## Roadmap

- **Phase 2**: Richer scene context (materials, named views, block instances)
- **Phase 3**: Iterative script refinement (Claude fixes errors and retries)
- **Phase 4**: Docked UI chat panel with markdown rendering
- **Phase 5**: Grasshopper component integration

## Troubleshooting

- **"No API key configured"** — Run `ClaudeSetKey` and enter your key.
- **"Network error"** — Check your internet connection and firewall settings.
- **"Python scripting engine not available"** — Ensure IronPython is installed in Rhino (Rhino 7) or the script editor is available (Rhino 8).
- **Script errors** — Review the generated script before running. Claude isn't perfect — review the code and use `Cancel` if something looks wrong.

## License

MIT — use and modify freely.
