# Lumi — Agent Guidelines

## Project Summary

Lumi is a cross-platform Avalonia desktop app — a personal agentic assistant that can do anything. It is a chat application with a modern, intuitive UX that feels alive. Lumi's main interface is a chat interface powered by GitHub Copilot SDK as the agentic backend. Single-project solution with MVVM architecture using CommunityToolkit.Mvvm source generators.

## Tech Stack

- **.NET 10** with C# and nullable reference types
- **Avalonia UI 11.3** — cross-platform desktop framework
- **CommunityToolkit.Mvvm 8.4** — MVVM source generators (`[ObservableProperty]`, `[RelayCommand]`)
- **GitHub.Copilot.SDK** — agentic backend for LLM interaction
- **StrataTheme** — custom UI component library (external project reference at `../../../Strata/src/StrataTheme/`)

## Core Concepts

- **Chat** — A chat session with Lumi. Primary interaction surface. Has message history and can be linked to a project and/or agent.
- **Project** — Named collection of chats with custom instructions injected into the system prompt.
- **Skill** — Reusable capability definition in markdown (e.g., "Word creator" converts markdown to Word via Python). Listed in system prompt so the LLM knows what's available.
- **Lumis (Agents)** — Custom agent personas combining system prompt, skills, and tools (e.g., "Daily Lumi" checks mail/todos and plans the day). Users create and select them.
- **Memory** — Persistent facts extracted from conversations, included in system prompt across all sessions.

## User Flows

- **Onboarding** — First launch asks the user's name, then greets them personally.
- **Chat** — Streaming chat with tool call visualization, reasoning display, typing indicators.
- **Memories** — Important info is remembered across sessions via system prompt injection.
- **Skills** — Users create skills and reference them from any chat.
- **Agents** — Users create agents and select them in the agents tab.
- **Projects** — Chats are organized into projects with custom instructions.
- **Context awareness** — `SystemPromptBuilder` assembles: active project, agent, time of day, user name, skills, and memories.

## UX Principles

- Modern and alive — animated components, responsive interactions, StrataTheme design system.
- Not bloated — main interface focuses on chats with clean navigation.
- Welcome experience — elegant welcome panel with suggestion chips.
- Transparency — tool calls grouped with friendly names, reasoning tokens displayed, streaming indicators.
- Dedicated management — agents, skills, projects each have master-detail CRUD with search.

## Architecture

```
App.axaml.cs
  ├── DataStore          (JSON persistence → %AppData%/Lumi/data.json)
  ├── CopilotService     (GitHub Copilot SDK wrapper)
  └── MainViewModel
        ├── ChatViewModel      → DataStore, CopilotService, SystemPromptBuilder
        ├── SkillsViewModel    → DataStore
        ├── AgentsViewModel    → DataStore
        ├── ProjectsViewModel  → DataStore
        └── SettingsViewModel  → DataStore
```

- **Models** (`src/Lumi/Models/Models.cs`): All domain entities in one file — `Chat`, `ChatMessage`, `Project`, `Skill`, `LumiAgent`, `Memory`, `UserSettings`, `AppData`
- **Services** (`src/Lumi/Services/`): `CopilotService` (SDK wrapper with streaming events), `DataStore` (JSON persistence to `%AppData%/Lumi/data.json`), `SystemPromptBuilder` (composite system prompt)
- **ViewModels** (`src/Lumi/ViewModels/`): `MainViewModel` (root), `ChatViewModel` (streaming chat), `AgentsViewModel`, `ProjectsViewModel`, `SkillsViewModel`, `SettingsViewModel` — all CRUD follows same pattern
- **Views** (`src/Lumi/Views/`): Avalonia XAML + code-behind. `ChatView.axaml.cs` is the heaviest — builds transcript programmatically using Strata controls
- **External dependency**: StrataTheme UI library referenced via `StrataPath` in `Lumi.csproj` — provides `StrataChatShell`, `StrataChatMessage`, `StrataMarkdown`, `StrataThink`, `StrataAiToolCall`, etc.

> **WARNING**: There are TWO copies of StrataTheme. The csproj first checks `../../../Strata/src/StrataTheme/` (a sibling repo — **primary**, used by build) then falls back to `../../Strata/src/StrataTheme/` (git submodule at `Strata/` — **stale fallback**). **Always edit Strata files in the primary external repo**, not in the `Strata/` submodule inside this repo. Edits to the wrong copy silently have no effect.

### Key Patterns

- **MVVM** with CommunityToolkit.Mvvm source generators — use `[ObservableProperty]` for bindable properties and `[RelayCommand]` for commands
- **Event-driven streaming** — `CopilotService` events → `Dispatcher.UIThread.Post` → ViewModel state → View reactivity
- **Programmatic UI construction** — `ChatView.axaml.cs` builds the chat transcript dynamically using Strata controls (not data templates)
- **JSON file persistence** — single `data.json` file via `DataStore`, no database
- **System prompt composition** — `SystemPromptBuilder` assembles context from user name, time of day, agent, project, skills, and memories

### Strata UI Controls Used

- `StrataChatShell`, `StrataChatComposer`, `StrataChatMessage` — chat layout
- `StrataMarkdown` — markdown rendering
- `StrataThink`, `StrataAiToolCall` — tool call display
- `StrataTypingIndicator` — streaming indicator
- `StrataAttachmentList`, `StrataFileAttachment` — file attachments

## Project Structure

```
src/Lumi/
  ├── Models/Models.cs         — All domain entities (Chat, Project, Skill, LumiAgent, Memory, etc.)
  ├── Services/
  │   ├── CopilotService.cs    — GitHub Copilot SDK integration
  │   ├── DataStore.cs         — JSON persistence
  │   └── SystemPromptBuilder.cs — Dynamic system prompt assembly
  ├── ViewModels/              — MVVM ViewModels
  │   ├── MainViewModel.cs     — Root VM, navigation, chat list management
  │   ├── ChatViewModel.cs     — Active chat state, message streaming
  │   ├── AgentsViewModel.cs   — Agent CRUD
  │   ├── ProjectsViewModel.cs — Project CRUD
  │   ├── SkillsViewModel.cs   — Skill CRUD
  │   └── SettingsViewModel.cs — User settings
  └── Views/                   — Avalonia XAML views + code-behind
      ├── MainWindow.axaml(.cs) — App shell, sidebar, navigation
      ├── ChatView.axaml(.cs)   — Chat transcript (heavy code-behind)
      ├── AgentsView.axaml(.cs) — Agent management
      ├── ProjectsView.axaml(.cs) — Project management
      ├── SkillsView.axaml(.cs)  — Skill management
      └── SettingsView.axaml(.cs) — Settings page
```

## Domain Model

| Entity | Purpose |
|--------|---------|
| `ChatMessage` | Single message with role (user/assistant/system/tool/reasoning) |
| `Chat` | Conversation with message history, linked to project/agent |
| `Project` | Collection of chats with custom instructions |
| `Skill` | Reusable capability definition (markdown content) |
| `LumiAgent` | Custom agent persona with system prompt, skills, tools |
| `Memory` | Persistent user fact extracted from conversations |
| `UserSettings` | App preferences (name, theme, model) |
| `AppData` | Root container for all persisted data |

## Code Style

- C# with nullable reference types enabled, implicit usings
- `[ObservableProperty]` on fields (e.g., `string _name;` → generates `Name` property)
- `[RelayCommand]` on methods for bindable commands
- `partial void On<PropertyName>Changed()` for property change side effects
- All Copilot event handlers must dispatch to UI thread via `Dispatcher.UIThread.Post()`

## Build & Test

```bash
dotnet build src/Lumi/Lumi.csproj
cd src/Lumi && dotnet run
```

No test project exists yet. StrataTheme project must be available as a sibling repo (see `StrataPath` in `Lumi.csproj`).

### UI Testing with Avalonia MCP

Lumi has an Avalonia MCP server configured in `.vscode/mcp.json`. This gives you live access to the running app — you can see the UI, click buttons, type text, inspect controls, check bindings, and take screenshots. **Use it.**

**Every time you make a UI change, you must test it with the MCP tools.** Don't just build and hope it works — run the app, poke at it, and confirm your changes look and behave correctly. This is your primary way of verifying UI work since there are no UI tests.

#### Workflow

1. Run `dotnet tool restore` once to ensure the CLI tool is available
2. Start Lumi: `cd src/Lumi && dotnet run`
3. Use the MCP tools to verify your work

#### What to test and how

- **Did your control actually render?** Use `find_control` to search by name (`#MyControl`) or type (`Button`). If it's not found, something is wrong.
- **Are properties set correctly?** Use `get_control_properties` to check values, visibility, enabled state, dimensions — anything you set in XAML or code-behind.
- **Do bindings work?** Use `get_data_context` to check ViewModel state, and `get_binding_errors` to catch broken bindings. Binding errors are silent failures — always check.
- **Does interaction work?** Use `click_control` to press buttons, `input_text` to type into text fields, `set_property` to change values at runtime. Verify the app responds correctly.
- **Does it look right?** Use `take_screenshot` to capture the window or a specific control. Check layout, alignment, and visual appearance.
- **Is the tree structure correct?** Use `get_visual_tree` or `get_logical_tree` to verify parent-child relationships and nesting.
- **What's focused?** Use `get_focused_element` to check focus behavior after interactions.
- **Styles applied?** Use `get_applied_styles` to inspect CSS classes, pseudo-classes, and style setters on a control.

#### Control identifiers

Many tools take a `controlId` parameter. Three formats work:
- `#Name` — matches by `Name` property (e.g., `#SendButton`)
- `TypeName` — first control of that type (e.g., `TextBox`)
- `TypeName[n]` — nth control of that type, 0-indexed (e.g., `Button[2]`)

#### When to use it

- After adding or modifying any XAML or code-behind UI code
- After changing data bindings or ViewModel properties that affect the UI
- After styling changes — verify pseudo-classes and setters apply
- When debugging layout issues — inspect bounds, margins, and visibility
- When a feature "should work" but you're not sure — take a screenshot and see

## Key Conventions

- **Single JSON file persistence** — no database. New data collections go in `AppData` class in `Models.cs`
- **Chat transcript is built in code-behind** (`ChatView.axaml.cs`), not with data templates. New message types need a case in `AddMessageControl()`
- **System prompt assembly** — new context sources should extend `SystemPromptBuilder.Build()`
- **Tool display names** — add friendly mappings in `ChatView.axaml.cs` `GetFriendlyToolDisplay()` and `ChatViewModel.cs` `FormatToolDisplayName()`
- **CRUD ViewModels** follow identical master-detail pattern — `SelectedX`, `IsEditing`, `EditX` properties, `New/Edit/Save/Cancel/Delete` commands
- **Strata controls** — always use Strata UI components for chat elements. Inspect the StrataTheme source for API
- **Modify StrataTheme when needed** — if a UI change makes more sense as a StrataTheme feature or fix (new control, new property, style tweak, bug fix), go ahead and make the change directly in the primary StrataTheme project (sibling repo, not the `Strata/` submodule). Don't work around library limitations in Lumi when the right fix belongs in Strata.
- **No over-engineering** — this is a personal app, keep implementations simple and direct
