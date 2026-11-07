# Testing

| Suite | What it proves | Run |
|---|---|---|
| `tests/ShinyCommander.Core.Tests` (xunit v3) | The file system layer without UI: listing disk and archives, copy/move/delete/pack/extract, conflicts, search masks, multi-rename, directory compare | `dotnet test tests/ShinyCommander.Core.Tests` (~1s) |
| **`tests/ShinyCommander.UITests`** (xunit v3 + MAUI DevFlow) | **The real macOS (AppKit) app**: the keymap, panels, file operations, archives, views, dialogs, mouse clicks, ribbon, layout and leaks | `tests/ShinyCommander.UITests/run.sh` |

## UI tests

```bash
tests/ShinyCommander.UITests/run.sh                         # build the app, run everything (~5 min)
tests/ShinyCommander.UITests/run.sh -t FileOperationTests   # one class - any name fragment
tests/ShinyCommander.UITests/run.sh -t F5_copies            # one test (or every test matching)
tests/ShinyCommander.UITests/run.sh --no-build -t F5_copies # seconds: reuses the running test app
tests/ShinyCommander.UITests/run.sh --screens               # screenshot every screen/mode → artifacts/screens
tests/ShinyCommander.UITests/run.sh --explicit -t Perf      # the known-red performance checks
tests/ShinyCommander.UITests/run.sh --fresh / --quit        # relaunch the app first / quit it afterwards
```

### The fast loop

When you're fixing one thing, you shouldn't wait for the whole suite. The test app **stays running between runs**,
and the next run **attaches** to it in under a second:

- **Without a rebuild:** `run.sh --no-build -t <Test>` runs a single test in a couple of seconds.
- **After an edit:** `run.sh -t <Test>` does an incremental build (about a minute). The fixture sees the binary is
  newer than the running app and relaunches it.

The app is built into `tests/ShinyCommander.UITests/.app-build`, never `src/ShinyCommander.MacOS/bin|obj`, so a test run
doesn't fight your own build or the copy of the app you're using.

### It doesn't touch your desktop or your settings

- **No keystrokes on your machine.** Nothing is typed into the real desktop. Keys go into the app's own shortcut
  engine, and clicks go through the DevFlow agent inside the app, so you can keep working while it runs.
- **In-memory settings.** Launched with `SHINYCOMMANDER_UITEST=1`, the app keeps its settings in memory, so your real
  preferences are never read or written.
- **Throwaway folders.** Each test works in its own folder under `tests/ShinyCommander.UITests/artifacts/work`.
- **No Trash.** Deletes are permanent in test mode, so nothing lands in your Trash.

### How it works

```
 test process (net10.0, xunit v3)                     ShinyCommander.app (Debug, SHINYCOMMANDER_UITEST=1)
 ─────────────────────────────────                    ──────────────────────────────────────────────────
 AppFixture ── launch / attach (.uitest-app.json) ──▶  MAUI DevFlow agent :102xx
 AgentClient (one client = one mutation lease) ──────▶  ├─ ui/* : tree, tap, fill, key, screenshot
 Commander ── extension tools org.shiny.commander ───▶  └─ Shared/Testing/UiTestHooks.cs
                                                            state · key · type · navigate · cursor · mark
                                                            command · answer · real_dialogs · reset
```

- **Keys.** DevFlow can tap and fill but can't press keys, and ShinyCommander is a keyboard program. The app's `key`
  tool parses a gesture (`F5`, `Primary+Shift+K`, `Ctrl+Q`) into key strokes and feeds them to Shiny's
  `KeyboardShortcutEngine`, the same entry point the AppKit key source uses. A test therefore exercises the real
  keymap, including CanExecute handing a key back to the command line while it has text (`textFocused`, true by
  default, matching real use).
- **Settling.** Every tool waits until the app has settled before it returns the full state:
  - both listings are loaded;
  - no operation or shell command is running;
  - the quick view has loaded.

  Tests assert on that `AppState` (paths, items, cursor, marks, view modes, dialogs, focus) rather than sleeping.
- **Dialogs.** In test mode `IAppDialogs` is `UiTestDialogs`.
  - **Scripted (the default).** Every alert, confirm, prompt, choice and toast is recorded and answered from a queue
    filled with `App.Answer(...)`:
    - `<default>` accepts a prompt's suggestion, and `<cancel>` cancels;
    - `yes` / `no` answer a confirm;
    - for a choice, give an option's text;
    - anything else is typed into a prompt.

    An unanswered question is cancelled, so no test hangs on a modal.
  - **Real.** `App.RealDialogs()` shows the actual Shiny dialogs for that test, and the test drives them with
    `Ui.ClickInDialog`.
- **One lease.** The agent hands a mutation lease to one driver at a time, and every `maui devflow` CLI process is a
  new holder. Back-to-back CLI calls therefore fail with "Another DevFlow session is driving this app". The suite
  instead talks HTTP through a single `AgentClient`, claiming the lease once, with force.
- **Known state per test.** `Start(left, right)` fills the test's two folders and calls `reset`. That:
  - closes pages and any open dialog;
  - closes extra tabs;
  - puts both panels back in details view, with no tree, quick view or filter;
  - restores the default columns;
  - clears marks, the command line and the dialog log;
  - points the panels at the two folders.
- **Diagnostics.** `App.Diagnostics()` adds native view counts to the state (`NativeViews`, `NativeViewTypes`). Walking
  every NSView is slow, so it's off unless a test asks, and each reset turns it off again.
- **Failure evidence** goes to `artifacts/failures/<test>/`:
  - `state.json`;
  - a screenshot of each panel;
  - `visual-tree.json`.

### Layout

```
Driver/  AppFixture (launch/attach/build), Commander (the app's tools), Ui (DevFlow taps/fills/visibility),
         AppState, TestTree (folders, zips, tar.gz), UiTest (base class), TestConfig, Proc
Flows/   the tests
run.sh   build + run, filters, flags
```

### What is covered

| Class | Proves |
|---|---|
| `NavigationTests` | Cursor keys, Home/End, Enter and Backspace (the cursor returns to the folder you came from), Tab, history, Primary+←/→, `cd` and shell commands with console output, root, swap, target = source, the watcher picking up new files, and keys going to the command line while it has text |
| `SelectionTests` | Insert, Space (with folder size), Shift+↓, select/unselect all, invert, select group by mask, same extension, the status line counts |
| `FileOperationTests` | F5 (cursor, marked, nothing, cancelled, conflict skip/overwrite/keep both, new name), F6 (move, rename when both panels are on the same folder), F7 (nested), F8 (confirm, decline, recursive), F2 (refusing an existing name), Shift+F4 + editor |
| `ArchiveTests` | Browsing zip and tar.gz, the read-only badge, copying out of tar.gz, refusing a delete in a read-only archive, copying into a zip, Alt+F5 to tar.gz and its suggested name, Alt+F9 one folder per archive |
| `ViewTests` | Thumbnails per panel with grid cursor moves, Left/Right left to the command line in details, Ctrl+Q quick view (text, markdown, image, folder; follows the cursor; ends on Tab), stacked layout geometry, per-panel trees, the column chooser, hidden files, quick filter, sorting, branch view |
| `TabsAndToolsTests` | Tabs (open on folder, close, cycle, last can't close), lister (disk and archive), help, find, multi-rename, sync dirs, hotlist |
| `RealUiTests` | Both panels' trees open at once, each following its own folder and resizing with its splitter; clicking and double-clicking rows, the function key bar, every ribbon tab and button, the path box, the per-panel tree, thumbnail tiles, the real prompt/confirm/choice dialogs, keyboard focus (native first responder: the dialog field on open, the command line after a dialog or page), the Settings page |
| `LayoutTests` | Content that exists but can't be *seen*: the console text, choice dialog options, names with a tree open, the path label. `Ui.WhyHidden` clips an element against every ancestor |
| `LeakTests` | Round trips (lister, help, tabs, navigation, thumbnails, quick view, dialogs, reset) leave no native views behind |
| `ScrollTests` | A 2000-file folder stays virtualized; scroll steps, Down and PageDown keep the UI thread busy for less than a set budget (net of the test's own call overhead) |
| `ScreensTests` (opt-in) | Screenshots of 23 screens and modes for a visual review |
| `PerformanceTests` (explicit, known red) | How long F1/F3/back and tab switches keep the UI thread busy (see below) |

### Known issues the suite has pinned down

- **Page changes are slow on the macOS head** (`PerformanceTests`, explicit).
  - **On a fresh launch:** F1 takes ~2s and coming back ~2.5s.
  - **After a long session:** both grow several times over.
  - **Where the time goes:** almost all of it is AppKit detaching and reattaching the main page's ~1000 NSViews.
    Each of those views has its autoresizing mask translated into four Auto Layout constraints, about 4000 in all,
    because the Shell's split view puts the window's layout engine to work. `state.NativeViewTypes` shows the counts.
  - **Why it isn't fixed in the app:** turning translation off from the app zeroes every view's frame, so the fix
    belongs in maui-labs' AppKit containers.
- **The AppKit ScrollView sizing bug** ([maui-labs#830](https://github.com/dotnet/maui-labs/pull/830)). It collapsed
  the options of every choice dialog (overwrite/skip, the drive menu, the hotlist, the columns) to zero height.
  `ShinyAppDialogs` now sizes the options list itself until #830 ships. `LayoutTests.Choice_dialogs_show_their_options`
  guards it.

## Before a commit

```bash
dotnet test tests/ShinyCommander.Core.Tests
tests/ShinyCommander.UITests/run.sh
```
