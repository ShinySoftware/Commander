# ShinyCommander roadmap

Planned, not started. Each item lists what it is, how it fits the existing code, and what "done" means.

## 1. Customizable button bar + user-defined commands

**Goal:** TC's "Change button bar" and "user commands" (`usercmd.ini`): let people add their own buttons and
key bindings that run either a built-in command or an external program with parameters.

### Model (`ShinyCommander.Core`)

```csharp
public sealed record UserCommand(
    string Id,              // "em_open_in_vscode"
    string Title,           // "Open in VS Code"
    string Icon,            // motion icon name
    string? BuiltIn,        // "CopyCommand" - or null for an external program
    string? Program,        // "code"
    string? Arguments,      // "%P%N" - TC-style placeholders
    string? StartIn,        // "%P"
    string? Shortcut,       // "Primary+Alt+V"
    bool RefreshAfter);     // reread both panels when the program exits
```

* **Placeholders** (`CommandLineExpander`, unit tested, no MAUI):
  * `%P` source path, `%T` target path, `%N` name under cursor, `%O` name without extension, `%E` extension.
  * `%S` marked names (space separated, quoted), `%L` / `%F` temp list file of full / plain names.
  * `%A` asks for a value in a prompt.
* **Storage:** one JSON document via `Shiny.Extensions.Stores`, a `[Bind] string? UserCommands` holding
  serialized `List<UserCommand>` (System.Text.Json source-generated context to stay AOT-safe).

### Ribbon

* **Generation becomes runtime.** `tools/ribbon.py` turns into a C# `RibbonDefinition`: the same table as code, plus a
  **"Custom"** tab built from the user's commands. The macOS fallback bar is generated from it too, until Shiny 2.5's
  Ribbon fix ships.
* Once Shiny 2.5 is out, the AppKit head uses `shiny:Ribbon` like the others, and both are built from `RibbonDefinition`.

### Keyboard

* User shortcuts register through `IKeyboardShortcutService` at runtime, so they show up in the F1 reference,
  which is generated from the live registry.
* When a gesture is already taken, the editor warns and doesn't save it.

### Editor UI

* A new `CommandsPage`, opened from Settings → "Buttons & commands":
  * A Shiny `TableView` lists the commands, with drag-sort to reorder them.
  * The edit form has the icon picked from the motion icon set with a live preview, built-in vs program, the
    arguments with a placeholder cheat-sheet, and a shortcut capture field.
  * **Test** runs the command against the current selection.

### Done when

* A program button with `%P%N` launches with the right file on all three heads.
* A user shortcut fires it.
* The commands survive a restart.
* The expander has unit tests for every placeholder and for quoting.

## 2. Side-by-side file diff

**Goal:** replace today's "identical / different" alert (Compare Files, `Primary+Shift+F2`) with TC's
side-by-side compare: aligned lines, changed lines and intra-line changes highlighted, next/previous difference,
and copy a block left/right.

### Engine (`ShinyCommander.Core/Compare/TextDiff.cs`)

* Myers O(ND) line diff with a patience pre-pass so moved blocks line up. Output:
  `IReadOnlyList<DiffHunk>` of `Equal / Inserted / Deleted / Modified` line ranges.
* For `Modified` line pairs, a character-level diff for the intra-line highlight.
* Options: ignore whitespace, ignore case, ignore line endings.
* Binary files fall back to a hex diff (16-byte rows, same aligner).
* Big files: diff on line hashes and cap at ~200k lines with a "showing first N" notice. It runs on a background
  thread with cancellation.
* Fully unit tested: empty files, identical files, pure insert/delete, CRLF vs LF, a moved block.

### UI (`ComparePage` → new `DiffPage`, `DiffViewModel`)

* **Layout:** two synchronized `CollectionView`s with fixed row height (one row per aligned line, blank filler rows
  for gaps) plus a thin overview strip that marks the differences.
* **Scrolling:** one panel's scroll drives the other.
* **Rows:** the line number, the text in a mono font, and the background colour for its kind. Intra-line changes are
  a `FormattedString` of spans.
* **Keys:**
  * `F7` / `Shift+F7` next / previous difference.
  * `Alt+→` / `Alt+←` copy the block across.
  * `Primary+S` saves; the title shows `*` while there are unsaved edits.
* **Inputs:**
  * Files from disk or from inside archives (via `VirtualFileSystem.MaterializeAsync`); writing back is disk-only.
  * The entry point is unchanged: two marked files, or the cursor file in each panel.
  * The sync dirs page gets a "Compare" action per row.

### Done when

* Two versions of a source file show aligned, with changes highlighted.
* Next/previous difference works.
* Copying a block and saving writes the file.
* The engine tests pass.
* The view handles a 50k-line file without stalling the UI.
