# MiniGui Application Implementation Document

## 1. Purpose

MiniGui is a minimal Microsoft Windows WPF graphical operator interface for running predefined OpenTAP test plans.

MiniGui is intended for production operators who need to:

1. launch the application through OpenTAP,
2. load or receive a predefined `.TapPlan`,
3. start execution,
4. stop execution when needed,
5. view live OpenTAP log messages,
6. view live OpenTAP published results, and
7. view the final test verdict.

MiniGui is not a replacement for OpenTAP. It is not a test plan editor, package manager, results browser, report generator, or custom execution engine.

Use the OpenTAP TUI project only as an architectural reference where useful:

```text
https://github.com/StefanHolst/opentap-tui
```

---

## 2. Target Platform

- Operating system: Microsoft Windows.
- UI technology: WPF.
- Language: C#.
- OpenTAP target version: OpenTAP 9.21.0.
- Distribution model: OpenTAP package/plugin.
- Launch model: through `tap.exe` using a custom OpenTAP command.

The implementation must verify the exact OpenTAP 9.21.0 APIs for:

- package metadata and package layout,
- custom `tap` command registration,
- test plan loading,
- test plan execution,
- stop/abort behavior,
- log listener APIs,
- result listener APIs,
- `Publish` result delivery,
- verdict reporting,
- optional user input APIs.

Do not assume API names or behavior from older OpenTAP versions.

---

## 3. Product Scope

MiniGui provides a small graphical layer over OpenTAP execution.

OpenTAP remains responsible for:

- loading test plans,
- executing test steps,
- sequencing,
- resource and instrument handling,
- logging,
- result publishing,
- verdict calculation,
- plugin discovery,
- configuration.

MiniGui is responsible only for:

- launching a simple WPF operator window,
- loading or receiving a selected `.TapPlan`,
- starting execution through OpenTAP,
- requesting controlled stop through OpenTAP,
- displaying execution state,
- displaying final verdict,
- displaying live OpenTAP logs,
- displaying live OpenTAP published results.

Core rule:

```text
MiniGui adds a GUI; it does not recreate OpenTAP.
```

---

## 4. Launch Model

MiniGui shall provide a custom OpenTAP command:

```text
tap minigui
```

The command should optionally accept a `.TapPlan` path:

```text
tap minigui "C:\TestPlans\ProductionTest.TapPlan"
```

Initial behavior:

- If a plan path is supplied, MiniGui loads that plan.
- If no plan path is supplied, MiniGui may show a simple file picker or display a clear error message with usage instructions.
- For the initial production-focused version, command-line plan selection is preferred.

MiniGui must not start a separate external process that runs `tap.exe` and parses console output. It must run inside the OpenTAP runtime as a package/plugin.

Conceptual runtime structure:

```text
Windows
  -> tap.exe
      -> OpenTAP Runtime
          -> OpenTAP Engine
              -> MiniGui WPF Plugin
```

---

## 5. Project Structure

Use a single C# project unless there is a clear reason to split the implementation.

Recommended structure:

```text
MiniGui/
  MiniGui.csproj
  package.xml or OpenTAP package metadata
  MiniGuiCommand.cs
  TestPlanController.cs
  MiniGuiLogListener.cs
  MiniGuiResultListener.cs
  MainWindow.xaml
  MainWindow.xaml.cs
  UserInputInterface.cs optional
```

A simple WPF code-behind approach is acceptable. MVVM may be used only if it reduces complexity.

Allowed dependencies:

- OpenTAP,
- .NET runtime supported by OpenTAP 9.21.0,
- WPF,
- standard .NET libraries.

Avoid unnecessary dependencies such as third-party UI frameworks, databases, web servers, IPC systems, charting libraries, and dependency injection containers unless they are strictly required.

---

## 6. Main Components

### 6.1 MiniGuiCommand

`MiniGuiCommand` integrates MiniGui with OpenTAP command execution.

Responsibilities:

- register the `tap minigui` command using the correct OpenTAP 9.21.0 API,
- parse command-line arguments,
- determine the `.TapPlan` path,
- initialize the WPF application,
- create and show `MainWindow`,
- connect the window to `TestPlanController`,
- return an appropriate command exit status when the window closes.

`MiniGuiCommand` should not contain test execution logic.

### 6.2 TestPlanController

`TestPlanController` coordinates the WPF UI and OpenTAP runtime.

Responsibilities:

- load the selected `.TapPlan`,
- expose current execution state,
- start execution using the supported OpenTAP API,
- request controlled stop using the supported OpenTAP API,
- attach and detach log listeners,
- attach and detach result listeners,
- report progress, errors, and final verdict,
- ensure cleanup on shutdown.

`TestPlanController` should not directly manipulate WPF controls. It should expose events, callbacks, bindable properties, or another simple abstraction that the UI can consume safely.

### 6.3 MainWindow

`MainWindow` is the WPF operator window.

Minimum visible UI:

- selected test plan path or name,
- current execution state,
- final verdict/status indicator,
- `START` button,
- `STOP` button,
- live Log panel,
- live Results panel.

The interface should remain simple and production-oriented.

### 6.4 MiniGuiLogListener

`MiniGuiLogListener` receives OpenTAP log events and forwards them to the UI.

Conceptual flow:

```text
OpenTAP Logging
  -> MiniGuiLogListener
      -> WPF Dispatcher
          -> Log Panel
```

Display available log fields such as:

- timestamp,
- level,
- source/category,
- message.

Use OpenTAP logging APIs directly. Do not parse console output.

### 6.5 MiniGuiResultListener

`MiniGuiResultListener` receives OpenTAP result events produced when test steps publish results.

Conceptual flow:

```text
TestStep calls OpenTAP Results.Publish(...)
  -> OpenTAP result listener callback
      -> MiniGuiResultListener
          -> WPF Dispatcher
              -> Results Panel
```

The Results panel must update live while the test is running. It must not wait until the test plan completes.

Use the supported OpenTAP 9.21.0 result listener mechanism. OpenTAP versions commonly expose published data through a result-listener callback receiving a result table, but exact class and method names must be verified before implementation.

Do not parse logs to create result rows. Do not read result files after execution to simulate live results.

---

## 7. WPF UI Behavior

### 7.1 Log Panel

The Log panel shall be a simple read-only scrolling view.

Requirements:

- show OpenTAP log messages live,
- retain messages from plan loading and across runs until manually cleared (subject to the line cap),
- attach the log listener before loading the plan and detach it when the window closes,
- auto-scroll to newest messages unless the operator has scrolled away,
- optionally cap retained lines to prevent unbounded memory growth,
- marshal all updates through the WPF Dispatcher.

### 7.2 Results Panel

The Results panel shall display data published through OpenTAP `Publish`.

Use a simple table or grid.

Suggested columns:

- Time,
- Test Step or Source,
- Result Table or Measurement Name,
- Field or Column,
- Value,
- Unit, if available,
- Verdict or Status, if available.

Display only fields actually available from the OpenTAP result callback. Do not invent missing values.

Acceptable display strategies:

1. one row per published scalar value,
2. one row per result-table row,
3. one compact textual row for complex published tables.

The first version does not require sorting, filtering, grouping, graphs, export, reporting, or historical browsing.

Minimum behavior:

- clear previous results when a new run starts,
- append published results live while the run is active,
- auto-scroll to the latest result unless the operator has scrolled away,
- marshal all UI updates through the WPF Dispatcher,
- detach the listener when the run ends or the window closes,
- avoid unbounded memory growth during high result volume.

---

## 8. Execution Lifecycle

Typical lifecycle:

```text
Start MiniGui
  -> attach log listener
  -> load TapPlan
  -> show Ready state
  -> operator presses START
  -> clear previous results (keep logs)
  -> attach result listener
  -> execute plan through OpenTAP
  -> update Log panel live
  -> update Results panel live from Publish callbacks
  -> receive completion/final verdict
  -> show PASS/FAIL/ERROR/STOPPED
```

MiniGui must not implement a custom test execution engine.

If OpenTAP requires a specific mechanism such as `TapThread` or another execution API, the implementation must follow the OpenTAP 9.21.0 API.

---

## 9. START and STOP Behavior

### START

When `START` is pressed:

1. verify that a valid `.TapPlan` is loaded,
2. verify that no test is already running,
3. clear run-specific UI state,
4. retain the Log panel, including startup and previous-run diagnostics,
5. clear the Results panel,
6. attach or enable the result listener,
7. start execution through OpenTAP,
8. update state to `Running`.

### STOP

When `STOP` is pressed:

1. request a controlled stop through OpenTAP,
2. update state to `Stopping`,
3. disable another `START` while stopping,
4. wait for OpenTAP execution to terminate normally,
5. update state to `Stopped`, `Completed`, `Passed`, `Failed`, or `Error` as appropriate.

MiniGui must not kill the entire process just because the operator presses `STOP`.

---

## 10. Threading Requirements

OpenTAP callbacks may occur on non-UI threads.

All WPF updates must be marshalled through the WPF `Dispatcher`.

This applies to:

- state changes,
- log messages,
- result rows,
- final verdict,
- error messages,
- optional user-input prompts.

The implementation must avoid:

- cross-thread WPF exceptions,
- UI deadlocks,
- blocking OpenTAP execution because the UI is busy,
- unbounded memory growth from long-running tests.

For high event volume, use a bounded collection, a virtualized control, batching, or dispatcher throttling.

---

## 11. State Model

Use a small explicit state model.

Suggested enum:

```csharp
public enum MiniGuiState
{
    Startup,
    Loading,
    Ready,
    Running,
    Stopping,
    Completed,
    Passed,
    Failed,
    Stopped,
    Error
}
```

Button enablement and status display should derive from this state.

A failing test verdict is not the same as a MiniGui application error.

---

## 12. Error Handling

Handle and clearly display:

- missing plan path,
- invalid plan path,
- plan-loading errors,
- OpenTAP initialization errors,
- execution errors,
- listener errors,
- normal failing test verdicts,
- operator stop.

Log exceptions and show useful error messages. Do not silently swallow exceptions.

---

## 13. Shutdown Behavior

When the WPF window closes:

1. check whether a test is running,
2. if running, request a controlled OpenTAP stop,
3. wait for appropriate shutdown according to OpenTAP APIs,
4. detach log listener,
5. detach result listener,
6. release test plan references,
7. exit cleanly.

MiniGui must not leave an OpenTAP execution running after the UI closes.

---

## 14. Optional User Input

Some OpenTAP test plans may request operator input.

Optional support may be added through OpenTAP's `IUserInputInterface`, if required.

Examples:

- barcode scan,
- serial number entry,
- fixture confirmation,
- simple pass/fail confirmation,
- visual inspection prompt.

Keep this feature small if implemented. Use OpenTAP TUI only as a reference for threading and behavior. Do not build a large dialog framework unless required.

---

## 15. Out of Scope

MiniGui must not include:

- test plan editing,
- test step editing,
- saving modified plans,
- package management,
- plugin management,
- resource or instrument configuration editing,
- advanced result browsing,
- charting,
- report generation,
- database/history storage,
- remote execution,
- user accounts,
- web UI,
- custom test execution engine,
- second logging framework.

Product rule:

```text
If a feature is not required to execute and monitor a predefined test plan, it probably does not belong in MiniGui.
```

---

## 16. Implementation Sequence

Recommended sequence:

1. Verify OpenTAP 9.21.0 APIs and runtime requirements.
2. Create the minimal OpenTAP package.
3. Implement the `tap minigui` command.
4. Launch a basic WPF `MainWindow`.
5. Load and display the `.TapPlan` path/name.
6. Implement `START` using OpenTAP's supported execution API.
7. Implement controlled `STOP`.
8. Display execution state and final verdict.
9. Implement OpenTAP log listener and live Log panel.
10. Implement OpenTAP result listener and live Results panel for `Publish` data.
11. Add error handling and cleanup.
12. Add optional user input only if needed.

---

## 17. Testing Requirements

Test with OpenTAP plans that verify:

- valid plan execution,
- invalid plan path,
- plan-loading error,
- normal passing verdict,
- normal failing verdict,
- execution exception,
- controlled `STOP`,
- closing the GUI while running,
- live log display,
- high log volume,
- live result display from `Publish`,
- high result volume,
- multiple published result tables,
- plan with no published results,
- optional user input if implemented.

For the Results panel, use a simple test step that publishes known values with visible delays between publishes. This must prove that the WPF Results panel updates live during execution, not only after completion.

---

## 18. Final Implementation Summary

MiniGui shall be a minimal Windows WPF OpenTAP package launched as:

```text
tap minigui "C:\TestPlans\ProductionTest.TapPlan"
```

The application shall provide only:

- plan display,
- `START`,
- `STOP`,
- execution state,
- final verdict,
- live OpenTAP logs,
- live OpenTAP published results.

The live Results panel is required and must be driven by OpenTAP result listener callbacks from `Publish`, not by log parsing or post-run file reading.

Keep the implementation small, direct, production-oriented, and compatible with OpenTAP 9.21.0.
