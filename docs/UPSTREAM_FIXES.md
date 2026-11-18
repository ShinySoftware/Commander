# Upstream fixes

Bugs in libraries ShinyCommander depends on, found while building the F4 editors (0.6.0-beta). Each one is worked
around here. The fixes belong in the library that has the bug; this is the plan for making them, and for taking the
workaround out of ShinyCommander once a fixed version ships.

Each item lists where the bug is, how to reproduce it, the fix, how to know it's fixed, and the workaround to remove.

| # | Library | Bug | Severity |
|---|---|---|---|
| 1 | Shiny.Maui.Controls - ImageEditor | `EditedImage.ToStreamAsync` returns `Stream.Null` on desktop | **High** - saving edits impossible on macOS, Windows, Linux |
| 2 | Shiny.Maui.Controls - Dialogs | `DialogView.FocusInput` + a second focus confirms the prompt on AppKit | **High** - every prompt confirmed itself empty |
| 3 | Shiny.Maui.Controls - DataGrid | Default cell ignores `DataGridColumn.ValueGetter` | Medium |
| 4 | Shiny.Maui.Controls.Diagram | Theme probes never resolve on AppKit | Medium |
| 5 | Shiny.Maui.Controls.Diagram | `NodeTemplate` hands the template the node, and the node outline/selection disappear | Low - docs, or a design change |
| 6 | Shiny.Maui.Shell | `[ShellMap]` on a partial class split over files generates duplicate routes | Medium |
| 7 | Shiny.Maui.Controls - docs | ImageEditor `Source` documented as `byte[]`; it's `ImageSource` | Low |
| 8 | ExifLibNet | ASCII tags written with a character count, not a byte count | Medium |
| 9 | ExifLibNet | IFD entries written in insertion order, not ascending tag order | Medium |
| 10 | ExifLibNet | `Properties.Set(tag, string)` adds instead of replacing | Low |
| 11 | ExifLibNet | TIFF saves are unreadable; PNG metadata isn't written | Medium - why only JPEG is editable |
| 13 | Shiny.Maui.Controls.Office | `SpreadsheetView` takes ~3 s to show and ~1.5 s to remove on AppKit, even for a 3-row sheet | Medium - the Excel preview stalls the cursor |
| 14 | Shiny.Maui.Controls - Ribbon | No pressed (mouse down) state; a checked button loses its hover | Low - feedback only |
| 15 | maui-labs - macOS head | `PointerGestureRecognizer` never raises `PointerPressed` / `PointerReleased` | Low - worked around |
| 16 | maui-labs - macOS head | Arranging an `Entry` that's being edited raises `Completed` | Medium - ended in-place renames, worked around |
| 12 | TagLibSharp | A constant bitrate MP3's "Info" header is ignored, and the file is timed by its first frame | Medium - low-bitrate MP3s showed half their length |

---

## Shiny.Maui.Controls

### 1. ImageEditor: no export on desktop

**Where:** `Shiny.Maui.Controls.ImageEditor.EditedImage.Render` - in the `net10.0` (AppKit, GTK4) and
`net10.0-windows10.0.19041` builds it replays the edit actions onto an `ImageEditorDrawable` and then
`return Stream.Null;`. Only the iOS/Android builds render.

**Repro:** an `ImageEditor` on the macOS head, any edit, the toolbar's Save → `SaveCommand` gets an `EditedImage` whose
`ToStreamAsync(...)` stream is empty.

**Fix:** render the drawable into an offscreen canvas and encode it, per platform:
- **Windows** - Win2D `CanvasRenderTarget` (Microsoft.Maui.Graphics.Win2D is already there), `SaveAsync` to PNG/JPEG.
- **macOS AppKit** - a `CGBitmapContext` wrapped in Microsoft.Maui.Graphics' `PlatformCanvas`, then
  `CGImageDestination` for PNG/JPEG (and WebP where ImageIO supports it).
- **GTK4** - a Cairo `ImageSurface` with the Gtk Microsoft.Maui.Graphics canvas, PNG via Cairo, JPEG via GdkPixbuf.

Honour `width`/`height` (scale the drawable) and `quality`. If a format can't be encoded on a platform, throw
`NotSupportedException` rather than returning an empty stream - an empty stream looks like success.

**Done when:** a test per head renders a known edit (rotate 90 + a stroke) and the decoded output has the rotated size
and the stroke's pixels.

**Remove here:** the empty-stream check in `ImageMetadataViewModel.SaveImage`
(`src/ShinyCommander.Shared/ViewModels/ImageMetadataViewModel.cs`, around line 225) and the `ImageMeta_ExportUnsupported`
string; put "Edit image" on the website's photo section; drop the either/or branch in
`FileEditorTests.F4_on_a_jpeg_edits_its_metadata` so it always asserts the photo changed and kept its title.

### 2. Dialogs: a prompt confirms itself on AppKit

**Where:** `DialogManager.ShowSingleAsync` now calls `DialogView.FocusInput()` after `AnimateInAsync` (new in 2.5.0).
On the maui-labs AppKit head an `NSTextField` that loses first responder ends editing, and MAUI raises that as
`Entry.Completed` - which the prompt treats as OK. Anything that moves focus after `FocusInput` (the app focusing
the field again, a toast, a keyboard shortcut) confirms the prompt with whatever is in it.

**Repro:** 2.4.0 app code that focuses the dialog's entry itself 120 ms after showing a prompt → on 2.5.0 the prompt
returns `Ok = true` with an empty value before it's ever seen.

**Fix (Shiny):** don't treat `Completed` as OK unless it came from Return. Either listen for the Return key (the
keyboard-shortcut engine already has key sources on AppKit/GTK) or check that the entry still has focus when
`Completed` fires - an end-of-editing caused by focus loss leaves it unfocused. Make `FocusInput` idempotent (do
nothing if the entry already has focus) so a second focus doesn't end the first edit.

**Fix (maui-labs, separate issue):** consider not raising `Completed` for an end-of-editing caused by losing first
responder (`NSTextMovement.Other`), only for Return (`NSTextMovement.Return`) - that's what iOS and Windows do.

**Done when:** a UI test shows a prompt, moves focus to another field and back, and the prompt is still open.

**Remove here:** nothing to undo - the fix was dropping ShinyCommander's own `FocusDialogEntry`, which is correct now
that Shiny focuses the field (the comment in `ShinyAppDialogs.cs` near line 117 explains it). Keep the
`RealUiTests.A_real_prompt_*` tests as the regression check.

### 3. DataGrid: the default cell ignores `ValueGetter`

**Where:** `DataGrid.BuildCellView` binds the cell label to `"Data." + column.PropertyName` through
`MemberPath.Binding`. `ValueGetter` (documented as the reflection-free accessor) is used for sorting, filtering,
grouping and editing, but never for display.

**Repro:** rows that are `object?[]`/a class with no matching property, columns with `PropertyName = "c0"` and a
`ValueGetter` → headers and paging work, every cell is blank.

**Fix:** when `ValueGetter` is set, don't bind by path - set the label text from
`column.FormatValue(column.GetCellValue(row.Data))` (on `BindingContext` changes, or a converter over `Data` that calls
the getter). That also makes the grid fully trim-safe when every column has a getter, which is what the docs promise.

**Done when:** a test grid with array rows and getter-only columns shows the values, sorts and edits.

**Remove here:** `CellText` and the two `column.CellTemplate = CellText(...)` lines in
`src/ShinyCommander.Shared/Views/DatabasePage.xaml.cs` (around lines 142, 199, 208).

## Shiny.Maui.Controls.Diagram

### 4. Theme probes never resolve on AppKit

**Where:** `DiagramPalette` - hidden zero-size `BoxView` probes with `SetDynamicResource(BoxView.ColorProperty,
"Shiny.Color.*")`. On the AppKit head every probe's `Color` stays `Transparent`, whether the token is defined
app-wide, on the page or on the `DiagramView` itself, so the palette falls back to its light defaults: a white canvas
in dark mode. (They're parented correctly - `BoxView > Grid > DiagramView > ... > Window` - and the resource is there.)

**Repro:** a `DiagramView` on the macOS head in dark mode.

**Fix:** stop relying on invisible views for colour lookup. Resolve the tokens directly -
`Application.Current.Resources.TryGetValue` walking up from the control through `IResourcesProvider` parents, which
is what `DynamicResource` does anyway - and re-resolve on `RequestedThemeChanged` and on the control's
`ResourcesChanged`. Then check whether `GanttPalette` and `ThemeProbe` (the docs say they use the same trick) have the
same problem on AppKit.

**Done when:** a dark-mode screenshot test of a diagram on AppKit shows a dark canvas, and a theme switch repaints it.

**Remove here:** `ThemeDiagram` and its `RequestedThemeChanged` hook in
`src/ShinyCommander.Shared/Views/DatabasePage.xaml.cs` (around lines 16-60) - reflection into the private
`Palette` property and the probe fields. Also delete the related line in the memory notes.

### 5. `NodeTemplate`: what the template gets, and what disappears

**Where:** `DiagramView.UpdateTemplateViews` sets the template's `BindingContext` to the `DiagramNode`, not the
node's `Item`. And with a template set, `DiagramDrawable` draws no node shape, so the node's outline, fill and selection
highlight go too, and the template has to draw its own selection.

**Fix:** a docs change at least - say the context is the `DiagramNode` (bind `Item.X`), and that the template replaces
the node's drawing, selection included. Better: keep drawing the selection outline around templated nodes (it's chrome,
not content), and add a `NodeTemplateBindsToItem` switch (or bind to `Item` when it's set) so templates can use compiled
bindings against the app's own type.

**Remove here:** `DiagramTableCard.xaml.cs` passes `node.Item` down and draws its own selection border; with the
change above, the card can bind straight to `DiagramTable` and drop `ShowSelection`.

### 7. Docs: ImageEditor `Source`

`image-editor.md` lists `Source` as `byte[]?` and the view-model pattern binds a `byte[]`. The property is an
`ImageSource`; a `byte[]` binding fails silently and the editor stays black. Fix the table and the sample
(`ImageSource.FromStream(() => new MemoryStream(bytes))`), or add a `byte[]` convenience property.

## Shiny.Maui.Shell

### 6. `[ShellMap]` on a partial class split over files

**Where:** the Shell source generator emits `Routes.X`, `NavigateToX` and `AddX` once per *syntax declaration* of a
class carrying `[ShellMap]`, so a view model split into `Foo.cs` + `Foo.Schema.cs` fails with CS0111 / CS0102.

**Fix:** collect by symbol, not by declaration - `ForAttributeWithMetadataName` on the type symbol, then
`.Collect()` and `Distinct(SymbolEqualityComparer.Default)` before emitting.

**Done when:** a generator test with a two-file partial `[ShellMap]` class produces one route.

**Remove here:** nothing breaks today - `DatabaseViewModel` was folded back into one file, with its helper types in
`DatabaseDesignerModels.cs`. Once fixed it can be split again (rows, schema designer, diagram) if that reads better.

## ExifLibNet

[oozcitak/exiflibrary](https://github.com/oozcitak/exiflibrary), 2.1.4 (MIT, last released 2021). Fix upstream if it's
still maintained; otherwise fork, or replace it with our own JPEG APP1 writer (a TIFF IFD serializer is a few hundred
lines, and it's the only part used).

### 8. ASCII tags: character count instead of byte count

**Where:** `ExifAscii.Interoperability` passes `mValue.Length + 1` (characters) as the count but writes
`Encoding.GetBytes(...)` bytes. Any non-ASCII text ("Lac Léman", "©") is written with a count that's too short, so
readers truncate it or reject the IFD.

**Fix:** count the encoded bytes - `var bytes = ExifBitConverter.GetBytes(mValue, true, Encoding); count = bytes.Length`.

**Remove here:** the `Utf8Ascii` class and the `.Select(... new Utf8Ascii(...))` in `ImageMetadata.Save`
(`src/ShinyCommander.Core/Metadata/ImageMetadata.cs`, around lines 179 and 226).
`ImageMetadataTests.Written_fields_read_back_and_the_picture_is_untouched` covers it.

### 9. IFD entries out of order

**Where:** `JPEGFile` (and `TIFFFile`) write each IFD's entries in the order the properties were added. TIFF 6.0
requires ascending tag order, and macOS ImageIO skips an IFD that isn't sorted - new tags looked unsaved in Finder.

**Fix:** sort each IFD's entries by tag ID when writing (in `WriteIFD`).

**Remove here:** the `OrderBy(x => x.IFD).ThenBy(x => (int)x.Tag)` re-add loop in `ImageMetadata.Save` (around line 177).

### 10. `Set(tag, string)` adds a duplicate

**Where:** `ExifPropertyCollection.Set(ExifTag key, string value)` calls `Add(key, value, Encoding.UTF8)` instead of
`Set(key, value, Encoding.UTF8)`, so setting an existing tag leaves two copies.

**Fix:** one word - `Set`.

**Remove here:** nothing to remove; `ImageMetadata.SetText` uses the three-argument overload (comment around line 218).
`ImageMetadataTests.Writing_twice_replaces_instead_of_duplicating` stays as the check.

### 11. TIFF output unreadable, PNG metadata not written

**Where:** saving a `TIFFFile` after a change produced a file macOS ImageIO couldn't decode (and the EXIF sub-IFD
tags were missing on reading back); `PNGFile.Save` ignores EXIF properties - nothing is written (no `eXIf` chunk).

**Fix:** TIFF - rewrite strip/tile offsets when IFD0 grows (likely why the image data no longer lines up). PNG - write
EXIF as an `eXIf` chunk before the first `IDAT` (PNG 1.5 extension), and read it back.

**Done when:** round-trip tests for TIFF and PNG decode with ImageIO and keep every tag.

**Then here:** add `tif`, `tiff` and `png` to `ImageMetadata.Editable` (with a header check per format) - today
`ImageMetadata.CanEdit` takes JPEG only for this reason. HEIC/WebP would need a different library either way.

### 14. Ribbon: no pressed state, and checked hides hover

**Where:** `src/Shiny.Maui.Controls/Ribbon/RibbonItemView.cs` (controls repo, now on the net11 branch). `Attach` wires a
tap and `PointerEntered` / `PointerExited` only, and `Refresh` picks one fill: `SecondaryContainer` when checked,
else `SurfaceContainerHighest` when hovered. So a button never looks held down, and hovering a checked toggle
(Folder Tree with the tree showing) shows no change at all.

**Fix:** in `RibbonItemView`
- a `facePressed` / `chevronPressed` flag beside the hover ones, set from `PointerPressed`, cleared on
  `PointerReleased`, on `PointerExited`, and after `InvokeFace` / `OpenMenu` (heads that send no release).
- `Refresh` resolves four fills instead of two: pressed (any) → a new `SurfaceContainerHighest`-over-`OnSurface`
  pressed tone (Material's 10% state layer, or a new `ShinyThemeKeys.Color.RibbonPressed` token), checked + hovered →
  a stronger `SecondaryContainer`, checked → `SecondaryContainer` + the ring as today, hovered → as today.
- the chevron cell of a split button gets the same pressed fill.
- the Blazor ribbon (`Shiny.Blazor.Controls`) gets the matching `:active` and `.checked:hover` rules so both hosts
  agree.

Fold it into the net11 work, not a 2.5.x patch: it touches the theme tokens, which are changing there anyway.

**Done when:** a `RibbonItemView` test raises pressed/released on a toggle's recognizer and reads back the four
fills; the sample's ribbon page shows a held and a checked-hovered button on Windows and Linux.

**Remove here:** nothing has to go - Windows / Linux (the Shiny ribbon, `RibbonToggleButton`s bound to MainViewModel's
toggle properties) just pick up the states. The macOS ribbon is our own (`Views/RibbonButtonState.cs`) for as long as
the Shiny ribbon's body doesn't draw on AppKit; when it does, retire `AppKitRibbon` from `tools/ribbon.py` along with
`RibbonButtonState`.

## maui-labs

### 15. macOS: no PointerPressed / PointerReleased

**Where:** `platforms/MacOS/src/MacOS/Handlers/GestureManager.cs` - `MacOSPointerTrackingArea` sends only
`SendPointerEntered`, `SendPointerExited` and `SendPointerMoved`.

**Fix:** handle `MouseDown` / `MouseUp` on the tracking view and call `SendPointerPressed` / `SendPointerReleased`
(same reflection pattern), without swallowing the event so tap recognizers still fire.

**Remove here:** the `#if MACOS_APPKIT` `NSEvent` monitor in `Views/RibbonButtonState.cs` (`WatchMouse` / `IsUnder`);
its `PointerPressed` / `PointerReleased` handlers then do the job. Fixes #14's pressed state on macOS too, once the Shiny
ribbon draws there.

### 16. macOS: arranging an Entry being edited raises Completed

**Where:** `platforms/MacOS/src/MacOS/Handlers/EntryHandler.cs` (`OnEditingEnded`, line ~90). A layout pass that moves
an `NSTextField` while it's being edited (`MacOSViewHandler.UpdateGeometry` → `setFrame:`) makes AppKit end the edit;
the handler passes `textDidEndEditing` straight on as `IEntry.Completed`, though the field still has the keyboard.

**Repro:** an `Entry` added to a row of a `CollectionView` (or a `HorizontalStackLayout`) and focused; let anything
re-arrange the row within the next few hundred ms - `Completed` fires with the text untouched, intermittently.

**Fix:** in `OnEditingEnded`, read `NSTextMovement` from the notification: raise `Completed` for Return, and for
"other" only when the field no longer has the keyboard (`Window.FirstResponder` isn't the field or its field editor).
Begin editing again when the frame change ended it.

**Remove here:** `FilePanelView.StillEditing` and its use in `OnInlineEditRequested`; the in-place rename tests in
`FileOperationTests` / `TabsAndToolsTests` stay as the check (they failed about one run in two without it).

## Shiny.Maui.Controls.Office

### 13. `SpreadsheetView` is slow to attach and detach on AppKit

**Where:** adding a `SpreadsheetView` to the visual tree on the macOS AppKit head adds ~1,300 native views
(MacOSContainerView, MauiNSTextField, BorderNSView, MacOSGraphicsView) and blocks the main thread ~3 s; removing it
blocks ~1.5 s. It's the same with `ShowShell`, `ShowToolbar`, `ShowFormulaBar`, `ShowTitleBar`, `ShowStatusBar`,
`ShowBackstage` and `ShowCommentsPane` all false, and the same when one instance is reused with only `Workbook`
changed - so the hidden chrome is still built, or the grid itself is made of views. Opening the workbook takes ~40 ms
and building the control ~150 ms; it's the native tree. `DocumentView` lays out in ~70 ms by comparison.

**Reproduce:** the preview panel on a 3-row `.xlsx` (`PreviewPaneTests.Word_excel_and_powerpoint_open_read_only_in_their_viewers`),
or any page that adds a `SpreadsheetView` at runtime; `UiTestHooks`' `diag` counts native views (920 without it,
2,214 with it).

**Fix:** don't build (or don't add) the parts that are switched off; draw headers and cells on the canvas rather than
as views.

**Remove here:** nothing to remove - `ExcelFilePreviewer` (`Services/Previewers/OfficeFilePreviewers.cs`) carries a
comment pointing here. Check the timing again after the bump.

## TagLibSharp

[mono/taglib-sharp](https://github.com/mono/taglib-sharp), 2.3.0 (LGPL-2.1, used unmodified) - the custom columns'
duration, dimensions and tags.

### 12. MP3 "Info" header ignored

**Where:** `TagLib.Mpeg.XingHeader` only recognises the `Xing` tag. lame writes `Info` in the same place for a
constant bitrate file, so `XingHeader.Present` is false and `AudioHeader.Duration` falls back to the stream size over
the first frame's bitrate. lame writes that first (Info) frame at a higher bitrate when the file's own is too small to
hold it - a 32 kbit/s file's first frame is 64 kbit/s - so the file reads half as long (a 6 s file: 3.07 s).

**Reproduce:** `ffmpeg -f lavfi -i anullsrc=r=44100:cl=stereo -t 6 -c:a libmp3lame -b:a 32k low.mp3`, then
`TagLib.File.Create("low.mp3").Properties.Duration`.

**Fix:** accept `Info` as well as `Xing` in `XingHeader` (same layout), and take the duration from its frame count.

**Remove here:** `MpegFrames` and the block that calls it in `FileMetadataReader.ReadMedia`
(`src/ShinyCommander.Core/Metadata/FileMetadataReader.cs`). `FileMetadataReaderTests.Low_bitrate_mp3_is_timed_by_its_frame_count`
stays as the check.

---

## Order

1. **#2 Dialogs** and **#1 ImageEditor export** - user-visible: one broke every prompt (fixed here, but the trap is
   still in Shiny for every desktop app), the other leaves a feature that can't save.
2. **#3 DataGrid** and **#4 Diagram probes** - each a small change in Shiny with a workaround in this repo to delete.
3. **#6 Shell generator**, **#5 / #7 docs**.
4. **ExifLibNet #8-#11** - decide fork vs. own writer first; #8-#10 are a few lines each, #11 is the real work.
5. **Office #13** - measure first; the preview is usable, just slow to appear.
6. **Ribbon #14** - with the controls repo's net11 update; **maui-labs #15** whenever that head is next touched; **#16** first of the two - it's a real bug, not only a missing feature.
7. **TagLibSharp #12** - a one-line change upstream; the workaround here is small and well covered.

After each Shiny release: bump `ShinyControlsVersion` in `Directory.Packages.props`, remove the workaround listed
above, and run just the tests named in that item.
