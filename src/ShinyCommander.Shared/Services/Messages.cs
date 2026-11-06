using Shiny.Mediator;

namespace ShinyCommander.Services;

/// <summary>Published (via Shiny.Mediator) after anything changes files so every panel showing those folders refreshes.</summary>
public record FileSystemChangedEvent(IReadOnlyList<string> Directories) : IEvent;

/// <summary>Navigate the active panel to a folder (and optionally put the cursor on a file) - used by search results, hotlist etc.</summary>
public record GoToPathEvent(string Directory, string? FocusName = null, bool OtherPanel = false) : IEvent;
