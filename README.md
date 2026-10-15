# Browser Testing Harness

An AI-powered Playwright + SpecFlow test authoring and execution studio built with .NET 8 and Blazor Server.

The harness decouples **AI Agent Exploration & Authoring** from **Automated Test Execution**, enabling teams to author, inspect, and maintain reliable Playwright end-to-end tests using natural language while executing tests at native speed without LLM timeout risks.

---

## Architecture Overview

```
                      +---------------------------------------+
                      |       Browser Testing Harness         |
                      |            (Blazor Server)            |
                      +-------------------+-------------------+
                                          |
                   +----------------------+----------------------+
                   |                                             |
                   v                                             v
       [AI Authoring Mode]                             [Execution Mode]
   +-----------------------+                       +-----------------------+
   | Microsoft.Extensions  |                       |      dotnet test      |
   |         .AI           |                       |  (SpecFlow + Playwright)|
   +-----------+-----------+                       +-----------+-----------+
               |                                               |
               v                                               v
       [@playwright/mcp]                             [Visual Modes]
   Live browser exploration,                      - Headless
   locator discovery &                            - Visible Browser (Slow-Mo)
   SpecFlow code generation                       - Playwright Inspector (PWDEBUG)
```

1. **Exploration & Authoring Mode**:
   - The AI agent interacts with the live web application through the official `@playwright/mcp` Model Context Protocol server.
   - It performs autonomous exploratory testing, discovers robust CSS/role-based locators, and drafts standard SpecFlow `.feature` Gherkin files alongside C# Playwright step definitions (`*Steps.cs`).
2. **Test Execution Mode**:
   - Tests execute via standard `dotnet test` within `PlaywrightMcpHarness.Tests`.
   - Runs completely decoupled from the LLM, ensuring deterministic, fast test runs and zero AI timeout penalties.

---

## Key Features

- **Multi-LLM Provider Support**:
  - **Google Gemini** (`gemini-2.5-flash`, `gemini-1.5-pro`)
  - **Open WebUI** (local or hosted OpenAI-compatible endpoint)
  - **Ollama** (`qwen2.5-coder:14b`, `llama3`, etc.)
  - **LM Studio** (`local-model`)
  - **OpenAI Cloud** (`gpt-4o`)
- **Integrated Workspace Code Editor**:
  - Offline syntax highlighting for both SpecFlow Gherkin (`.feature`) and C# (`.cs`) step definitions.
  - Line numbers, Material Darker theme, and two-way file saving.
  - Distraction-free **Full Screen View** with one-click expansion and <kbd>Esc</kbd> shortcut.
  - Delete features and step definitions directly within the UI.
- **Visual Execution Modes**:
  - **Headless**: Maximum CI/CD speed.
  - **Visible Browser (Slow-Mo)**: Headed browser execution with 500ms delay per action for visual verification.
  - **Playwright Inspector**: Interactive step-by-step debugger with `PWDEBUG=1`.
- **Targeted & Parallel Execution**:
  - Filter and run individual features or execute the entire test suite.
  - Toggle between parallel execution and sequential mode.
- **AI Diagnostics & Self-Healing ("Fix with Agent")**:
  - When tests fail, a **Fix with Agent** button appears in both the Test Runner header and per-scenario cards.
  - Automatically captures the failure message, Playwright stack traces, and relevant file contents into an actionable diagnostic prompt for the AI to fix immediately.
- **Live Output & Scenario Analytics**:
  - In-browser terminal stream with clean ANSI formatting and auto-scroll controls.
  - Dedicated **Scenarios** tab breaking down pass/fail counts, execution duration, and failure reasons.

---

## Solution Structure

```
browser-testing-harness/
├── PlaywrightMcpHarness.sln
├── PlaywrightMcpHarness.Studio/         # Blazor Server Web UI & AI Orchestration
│   ├── Components/                      # Blazor components & pages (Studio.razor)
│   ├── Models/                          # Configuration & test result models
│   ├── Services/                        # MCP agent service & SpecFlow test runner
│   ├── wwwroot/                         # CodeMirror editor, Bootstrap, styles
│   └── appsettings.json                 # LLM provider configurations & MCP settings
├── PlaywrightMcpHarness.Tests/          # Executable SpecFlow + Playwright test suite
│   ├── Features/                        # Dynamic SpecFlow .feature files (.gitignored)
│   ├── StepDefinitions/                 # Playwright C# step definitions (.gitignored)
│   ├── Hooks/                           # SpecFlow before/after scenario hooks
│   ├── specflow.json                    # SpecFlow runtime configuration
│   └── sequential.runsettings           # Runsettings for sequential execution
└── README.md
```

---

## Prerequisites

1. **.NET 8 SDK** or later ([Download .NET 8](https://dotnet.microsoft.com/download/dotnet/8.0))
2. **Node.js** (v18+) and **npm**
3. **Playwright Browsers**:
   ```bash
   npx @playwright/mcp install-browser chrome-for-testing
   ```
   Or install via Playwright PowerShell script:
   ```powershell
   powershell PlaywrightMcpHarness.Tests/bin/Debug/net8.0/playwright.ps1 install
   ```

---

## Getting Started

### 1. Build the Solution
```bash
dotnet build PlaywrightMcpHarness.sln
```

### 2. Run the Studio
```bash
dotnet run --project PlaywrightMcpHarness.Studio --launch-profile "http"
```
The studio will launch at `http://localhost:5292`.

### 3. Configure Your LLM Provider
- Open the studio in your browser.
- Select your AI Provider from the top navigation dropdown (e.g., **Google Gemini**, **Open WebUI**, **Ollama**, **LM Studio**, or **OpenAI**).
- Enter your API Key in the top navbar input (keys can also be configured via environment variables or in `appsettings.json`).

### 4. Author Tests with Natural Language
Type a prompt in the AI chat:
> *"Navigate to https://example.com, verify the navigation links, and generate a SpecFlow feature and step definitions."*

The agent will explore the target page using the `@playwright/mcp` browser session and save `.feature` and `*Steps.cs` files directly into `PlaywrightMcpHarness.Tests`.

### 5. Run & Inspect Tests
- Choose an execution mode (**Visible Browser (Slow-Mo)**, **Headless**, or **Inspector**).
- Select a specific feature or run all features.
- Click **Run Tests** and monitor the live terminal output and Scenarios tab.

---

## License

MIT
