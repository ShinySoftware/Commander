using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using ShinyCommander.Core.Remote;

namespace ShinyCommander.Remote;

/// <summary>
/// WebDAV (RFC 4918) over plain HttpClient: PROPFIND lists, GET/PUT move content, MKCOL, DELETE and MOVE do the rest.
/// Basic and Digest sign-in through the handler. Nextcloud, ownCloud, Synology, IIS, Apache mod_dav, Shiny's own server...
/// </summary>
public sealed class WebDavProvider : IRemoteProvider
{
    public IReadOnlyList<RemoteProtocol> Protocols { get; } = [RemoteProtocol.WebDav, RemoteProtocol.WebDavs];

    public async Task<IRemoteFileSystem> ConnectAsync(RemoteConnection connection, string? secret, CancellationToken ct = default)
    {
        var handler = new HttpClientHandler { PreAuthenticate = true };
        if (connection.UserName.Length > 0)
            handler.Credentials = new NetworkCredential(connection.UserName, secret ?? String.Empty);

        var scheme = connection.Protocol == RemoteProtocol.WebDavs ? "https" : "http";
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri($"{scheme}://{connection.Host}:{connection.EffectivePort}"),
            Timeout = Timeout.InfiniteTimeSpan
        };
        var fs = new WebDavFileSystem(http);
        try
        {
            // the start folder proves both the address and the password
            var start = RemotePath.Split(connection.StartUri).Inner;
            await fs.PropFindAsync(start, 0, ct).ConfigureAwait(false);
        }
        catch
        {
            await fs.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return fs;
    }
}

sealed class WebDavFileSystem(HttpClient http) : IRemoteFileSystem
{
    static readonly XNamespace Dav = "DAV:";
    static readonly HttpMethod PropFind = new("PROPFIND");
    static readonly HttpMethod MkCol = new("MKCOL");
    static readonly HttpMethod Move = new("MOVE");

    const string PropFindBody = """
        <?xml version="1.0" encoding="utf-8"?>
        <d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/><d:getcontentlength/><d:getlastmodified/></d:prop></d:propfind>
        """;

    static string UrlOf(string inner, bool collection = false)
    {
        var path = String.Join('/', inner.Split('/').Select(Uri.EscapeDataString));
        return collection && !path.EndsWith('/') ? path + "/" : path;
    }

    internal async Task<List<(string Href, RemoteItem Item)>> PropFindAsync(string inner, int depth, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(PropFind, UrlOf(inner, depth > 0));
        request.Headers.Add("Depth", depth.ToString(CultureInfo.InvariantCulture));
        request.Content = new StringContent(PropFindBody, Encoding.UTF8, "application/xml");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        Check(response, inner);

        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return xml.Descendants(Dav + "response").Select(Parse).Where(x => x.Href.Length > 0).ToList();
    }

    static (string Href, RemoteItem Item) Parse(XElement response)
    {
        var href = response.Element(Dav + "href")?.Value ?? String.Empty;
        // servers send "/dav/a%20b" or "https://host/dav/a%20b" - and on Unix a bare "/path" parses as an absolute file:// URI
        if (href.StartsWith("http", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(href, UriKind.Absolute, out var absolute))
            href = absolute.AbsolutePath;
        href = Uri.UnescapeDataString(href);

        // the props that came back 200 - servers report the ones they don't have under a 404 propstat
        var props = response.Elements(Dav + "propstat")
            .Where(x => x.Element(Dav + "status")?.Value.Contains(" 200", StringComparison.Ordinal) ?? true)
            .Select(x => x.Element(Dav + "prop"))
            .Where(x => x != null)
            .Select(x => x!)
            .ToList();
        var isDir = props.Any(x => x.Element(Dav + "resourcetype")?.Element(Dav + "collection") != null);
        var size = props.Select(x => x.Element(Dav + "getcontentlength")?.Value).FirstOrDefault(x => x != null);
        var modified = props.Select(x => x.Element(Dav + "getlastmodified")?.Value).FirstOrDefault(x => x != null);

        var name = href.TrimEnd('/');
        name = name[(name.LastIndexOf('/') + 1)..];
        return (href, new RemoteItem(
            name,
            isDir,
            Int64.TryParse(size, out var s) ? s : 0,
            DateTime.TryParse(modified, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var m) ? m.ToLocalTime() : DateTime.MinValue
        ));
    }

    static void Check(HttpResponseMessage response, string inner)
    {
        if (response.IsSuccessStatusCode)
            return;

        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new RemoteAuthenticationException("The server refused the user name or password"),
            HttpStatusCode.NotFound => new FileNotFoundException($"{inner} not found", inner),
            HttpStatusCode.Forbidden => new UnauthorizedAccessException($"{inner}: access denied"),
            _ => new IOException($"{inner}: {(int)response.StatusCode} {response.ReasonPhrase}")
        };
    }

    public async Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken ct = default)
    {
        List<(string Href, RemoteItem Item)> found;
        try
        {
            found = await this.PropFindAsync(path, 1, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException ex)
        {
            throw new DirectoryNotFoundException(ex.Message, ex);
        }

        // the folder itself is in the answer too - the base address is the bare host, so hrefs are the same paths we ask for
        var self = path.Trim('/');
        return found
            .Where(x => x.Item.Name.Length > 0 && !String.Equals(x.Href.Trim('/'), self, StringComparison.Ordinal))
            .Select(x => x.Item)
            .ToList();
    }

    public async Task<RemoteItem?> GetItemAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var found = await this.PropFindAsync(path, 0, ct).ConfigureAwait(false);
            return found.Count == 0 ? null : found[0].Item;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
    {
        var response = await http.GetAsync(UrlOf(path), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        try
        {
            Check(response, path);
            return new ResponseStream(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public async Task WriteAsync(string path, Stream content, long length, DateTime? modified, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, UrlOf(path)) { Content = new StreamContent(content) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentLength = length;
        // Nextcloud / ownCloud keep the modified time from this
        if (modified is { } when)
            request.Headers.Add("X-OC-Mtime", new DateTimeOffset(when).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        Check(response, path);
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(MkCol, UrlOf(path, true));
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        Check(response, path);
    }

    public Task DeleteFileAsync(string path, CancellationToken ct = default) => this.DeleteAsync(UrlOf(path), path, ct);

    // DELETE on a collection removes everything under it (RFC 4918 9.6.1)
    public Task DeleteDirectoryAsync(string path, CancellationToken ct = default) => this.DeleteAsync(UrlOf(path, true), path, ct);

    async Task DeleteAsync(string url, string path, CancellationToken ct)
    {
        using var response = await http.DeleteAsync(url, ct).ConfigureAwait(false);
        Check(response, path);
    }

    public async Task RenameAsync(string from, string to, bool isDirectory, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(Move, UrlOf(from, isDirectory));
        request.Headers.Add("Destination", new Uri(http.BaseAddress!, UrlOf(to, isDirectory)).AbsoluteUri);
        request.Headers.Add("Overwrite", "F");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        Check(response, from);
    }

    public ValueTask DisposeAsync()
    {
        http.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>A response body that disposes its response along with it.</summary>
sealed class ResponseStream(Stream inner, IDisposable owner) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => inner.Read(buffer);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            owner.Dispose();
        }
        base.Dispose(disposing);
    }
}
