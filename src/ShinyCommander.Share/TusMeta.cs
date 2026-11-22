using System.Text;
using Shiny.Net.HttpServer.Tus;

namespace ShinyCommander.Share;

static class TusMeta
{
    /// <summary>A metadata value as text, or null when the client didn't send it (the indexer throws for that).</summary>
    public static string? Get(TusMetadata metadata, string key) => metadata.GetBytes(key) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;
}
