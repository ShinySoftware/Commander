using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShinyCommander.UITests.Driver;

/// <summary>What the app's <c>state</c> tool returns (Shared/Testing/UiTestHooks.cs) - the whole observable app.</summary>
public sealed record AppState
{
    public string? Page { get; init; }
    public string ActiveSide { get; init; } = "";
    public bool StackPanels { get; init; }
    public string CommandText { get; init; } = "";
    public string CommandOutput { get; init; } = "";
    public bool ShowOutput { get; init; }
    public bool IsOperationRunning { get; init; }
    public string? RibbonTab { get; init; }
    public List<string> Columns { get; init; } = [];
    public PanelState Left { get; init; } = new();
    public PanelState Right { get; init; } = new();
    public List<DialogRecord> Dialogs { get; init; } = [];
    public int PendingAnswers { get; init; }

    /// <summary>The Entry that really has the keyboard (native first responder): an AutomationId, "dialog", "(other)" or null.</summary>
    public string? Focus { get; init; }

    /// <summary>NSViews under the window (macOS) - page changes cost time in proportion to this.</summary>
    public int NativeViews { get; init; }
    public string? NativeViewTypes { get; init; }
    public int Shortcuts { get; init; }
    public long ManagedMb { get; init; }

    [JsonIgnore]
    public PanelState Active => this.ActiveSide == "left" ? this.Left : this.Right;

    [JsonIgnore]
    public PanelState Inactive => this.ActiveSide == "left" ? this.Right : this.Left;

    public PanelState Side(string side) => side == "left" ? this.Left : this.Right;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

public sealed record PanelState
{
    public bool IsActive { get; init; }
    public string Path { get; init; } = "";
    public string ViewMode { get; init; } = "";
    public bool ShowTree { get; init; }
    public bool ShowQuickFilter { get; init; }
    public bool IsQuickView { get; init; }
    public int ThumbnailSpan { get; init; }
    public List<string> Tabs { get; init; } = [];
    public int ActiveTab { get; init; }
    public QuickViewState QuickView { get; init; } = new();
    public int Cursor { get; init; }
    public string? CursorName { get; init; }
    public int MarkedCount { get; init; }
    public string? Status { get; init; }
    public string? Error { get; init; }
    public bool IsArchive { get; init; }
    public bool IsReadOnlyArchive { get; init; }
    public string? ArchiveBadge { get; init; }
    public string? QuickFilter { get; init; }
    public bool IsBranchView { get; init; }
    public string? Sort { get; init; }
    public bool CanGoBack { get; init; }
    public List<ItemState> Items { get; init; } = [];

    /// <summary>Names in display order, without the ".." row.</summary>
    [JsonIgnore]
    public List<string> Names => this.Items.Where(x => !x.Parent).Select(x => x.Name).ToList();

    [JsonIgnore]
    public List<string> Marked => this.Items.Where(x => x.Marked).Select(x => x.Name).ToList();

    public ItemState Item(string name) => this.Items.FirstOrDefault(x => x.Name == name)
        ?? throw new ShouldAssertException($"'{name}' is not listed in {this.Path} (has: {String.Join(", ", this.Names)})");
}

public sealed record QuickViewState
{
    public string Title { get; init; } = "";
    public string? Kind { get; init; }
    public string Info { get; init; } = "";
    public string Text { get; init; } = "";
    public bool HasImage { get; init; }
}

public sealed record ItemState
{
    public string Name { get; init; } = "";
    public bool Dir { get; init; }
    public bool Parent { get; init; }
    public bool Marked { get; init; }
    public long Size { get; init; }
    public string SizeText { get; init; } = "";
}

public sealed record DialogRecord
{
    public string Kind { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Message { get; init; }
    public string? Initial { get; init; }
    public List<string>? Options { get; init; }
    public string? Answer { get; init; }

    public override string ToString() => $"{this.Kind} '{this.Title}' {this.Message} → {this.Answer ?? "(unanswered)"}";
}
