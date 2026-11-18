using ShinyCommander.Core.Platform;

namespace ShinyCommander.Views;

/// <summary>
/// A page or a PDF in the platform's web view (HtmlFilePreviewer, PdfFilePreviewer). Links out of the preview open
/// in the browser instead of replacing it.
/// </summary>
public class WebPreviewView : WebView
{
    WebPreviewView() => this.Navigating += this.OnNavigating;

    public static WebPreviewView ForHtml(string html, string baseUrl) => new()
    {
        Source = new HtmlWebViewSource { Html = html, BaseUrl = baseUrl }
    };

    public static WebPreviewView ForFile(string path)
    {
#if MACOS_APPKIT
        // a file:// request is refused by WKWebView - files load through LoadFileUrl, with read access to the folder (OnHandlerChanged)
        return new WebPreviewView { file = path };
#else
        return new WebPreviewView { Source = new UrlWebViewSource { Url = new Uri(path).AbsoluteUri } };
#endif
    }

#if MACOS_APPKIT
    string? file;
    CoreGraphics.CGSize contentSize;
    Foundation.NSObject? frameObserver;

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        this.frameObserver?.Dispose();
        this.frameObserver = null;
        if (this.Handler?.PlatformView is not WebKit.WKWebView web)
            return;

        if (this.file != null)
            web.LoadFileUrl(Foundation.NSUrl.FromFilename(this.file), Foundation.NSUrl.FromFilename(Path.GetDirectoryName(this.file)!));

        // on the native frame itself - MAUI's SizeChanged comes before the handler has set it
        web.PostsFrameChangedNotifications = true;
        this.frameObserver = AppKit.NSView.Notifications.ObserveFrameChanged(web, this.OnFrameChanged);
        this.SizeContent();
    }

    void OnFrameChanged(object? sender, Foundation.NSNotificationEventArgs e) => this.SizeContent();

    // maui-labs sets a view's frame with AppKit's autoresizing turned off (MacOSViewHandler.UpdateGeometry), and the
    // WKWebView's own views - where the page is drawn - are sized only by autoresizing: they stayed 0x0 from when it
    // was created, and the preview showed a black box (a PDF just its toolbar). Resize them by what the frame changed,
    // as autoresizing would have. Remove once maui-labs leaves autoresizing on (UPSTREAM_FIXES #18)
    void SizeContent()
    {
        if (this.Handler?.PlatformView is not WebKit.WKWebView web || web.Bounds.Size == this.contentSize)
            return;

        web.ResizeSubviewsWithOldSize(this.contentSize);
        this.contentSize = web.Bounds.Size;
    }
#endif

    void OnNavigating(object? sender, WebNavigatingEventArgs e)
    {
        if (!Uri.TryCreate(e.Url, UriKind.Absolute, out var uri) || uri.IsFile || uri.Scheme is "about" or "data" or "blob")
            return;

        e.Cancel = true;
        if (uri.Scheme is "http" or "https" or "mailto")
            ShellLauncher.OpenWithDefaultApp(uri.AbsoluteUri);
    }
}
