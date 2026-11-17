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
        var view = new WebPreviewView();
#if MACOS_APPKIT
        // a file:// request is refused by WKWebView - files load through LoadFileUrl, with read access to the folder
        view.HandlerChanged += (_, _) =>
        {
            if (view.Handler?.PlatformView is WebKit.WKWebView web)
                web.LoadFileUrl(Foundation.NSUrl.FromFilename(path), Foundation.NSUrl.FromFilename(Path.GetDirectoryName(path)!));
        };
#else
        view.Source = new UrlWebViewSource { Url = new Uri(path).AbsoluteUri };
#endif
        return view;
    }

    void OnNavigating(object? sender, WebNavigatingEventArgs e)
    {
        if (!Uri.TryCreate(e.Url, UriKind.Absolute, out var uri) || uri.IsFile || uri.Scheme is "about" or "data" or "blob")
            return;

        e.Cancel = true;
        if (uri.Scheme is "http" or "https" or "mailto")
            ShellLauncher.OpenWithDefaultApp(uri.AbsoluteUri);
    }
}
