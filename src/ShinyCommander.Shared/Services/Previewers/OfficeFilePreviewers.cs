#if !LINUX_GTK
using Shiny.Controls.Office.Document;
using Shiny.Controls.Office.Presentation;
using Shiny.Controls.Office.Spreadsheet;
using Shiny.Maui.Controls.Office;

namespace ShinyCommander.Services.Previewers;

/// <summary>
/// Word, Excel and PowerPoint files in Shiny's Office viewers - read-only: no ribbon, no formula bar, no editing.
/// Each holds its file open until the panel moves on (PreviewHost). Not on Linux: the GTK head has no SkiaSharp canvas.
/// </summary>
public abstract class OfficeFilePreviewer(string key, Func<string> title, params string[] extensions) : IFilePreviewer
{
    const long MaxBytes = 64 * 1024 * 1024;

    public string Key => key;
    public string Title => title();
    public int Order => 100;

    public bool Handles(string fileName) => extensions.Contains(Path.GetExtension(fileName).TrimStart('.'), StringComparer.OrdinalIgnoreCase);

    // an OOXML file is a zip - an old binary .doc renamed .docx would only fail to open
    public bool CanPreview(string path)
    {
        if (new FileInfo(path).Length > MaxBytes)
            return false;

        Span<byte> header = stackalloc byte[4];
        using var stream = File.OpenRead(path);
        return stream.ReadAtLeast(header, header.Length, false) == header.Length && header.SequenceEqual("PK\u0003\u0004"u8);
    }

    public abstract Task<View> CreateView(string path, CancellationToken ct);
}

public sealed class WordFilePreviewer() : OfficeFilePreviewer("word", () => L.Preview_Word, "docx", "docm")
{
    public override async Task<View> CreateView(string path, CancellationToken ct)
    {
        var document = await WordDocument.OpenAsync(path, null, false, ct).ConfigureAwait(false);
        return await MainThread.InvokeOnMainThreadAsync<View>(() =>
        {
            var view = new DocumentView { Document = document, AutomationId = "PreviewWord" };
            return new PreviewHost(view, () =>
            {
                view.Dispose();
                document.Dispose();
            });
        });
    }
}

public sealed class PowerPointFilePreviewer() : OfficeFilePreviewer("powerpoint", () => L.Preview_PowerPoint, "pptx", "pptm")
{
    public override async Task<View> CreateView(string path, CancellationToken ct)
    {
        var deck = await SlideDeck.OpenAsync(path, null, false, ct).ConfigureAwait(false);
        return await MainThread.InvokeOnMainThreadAsync<View>(() =>
        {
            var view = new SlideView { Deck = deck, Mode = SlideViewMode.Single, AutomationId = "PreviewPowerPoint" };
            return new PreviewHost(view, () =>
            {
                view.Dispose();
                deck.Dispose();
            });
        });
    }
}

public sealed class ExcelFilePreviewer() : OfficeFilePreviewer("excel", () => L.Preview_Excel, "xlsx", "xlsm")
{
    public override async Task<View> CreateView(string path, CancellationToken ct)
    {
        var workbook = await Workbook.OpenAsync(path, null, ct).ConfigureAwait(false);
        return await MainThread.InvokeOnMainThreadAsync<View>(() =>
        {
            // the grid and its sheet tabs, nothing of Excel's window around them. Slow to show on the AppKit head
            // (docs/UPSTREAM_FIXES.md #13)
            var view = new SpreadsheetView
            {
                Workbook = workbook,
                IsReadOnly = true,
                AllowSheetEditing = false,
                ShowShell = false,
                ShowToolbar = false,
                ShowFormulaBar = false,
                ShowFileCommands = false,
                AutomationId = "PreviewExcel"
            };
            return new PreviewHost(view, () =>
            {
                view.Dispose();
                workbook.Dispose();
            });
        });
    }
}
#endif
