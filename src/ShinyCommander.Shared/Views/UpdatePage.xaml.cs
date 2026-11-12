namespace ShinyCommander.Views;

/// <summary>
/// "A new version is available": the release's version and notes, with Install and Later. Shown in a window of its
/// own when a check finds a release, and from Settings.
/// </summary>
/// <remarks>
/// "Don't show again" skips this release only, and takes effect however the window closes (Later, Install failing,
/// the close button).
/// </remarks>
public partial class UpdatePage : ContentPage
{
    readonly UpdateService updates;
    readonly AppRelease release;

    public UpdatePage(UpdateService updates, AppRelease release)
    {
        this.updates = updates;
        this.release = release;
        this.InitializeComponent();

        this.VersionLabel.Text = $"Version {release.DisplayVersion} - you have {updates.CurrentVersion}";
        this.ReleasedLabel.Text = $"Released {release.PublishedAt.LocalDateTime:D}";
        this.Notes.Markdown = release.Notes.Length > 0 ? release.Notes : "_No release notes._";
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (this.DontShow.IsChecked)
            this.updates.Skip(this.release);
    }

    void OnGitHubTapped(object? sender, TappedEventArgs e) => _ = Launcher.OpenAsync(this.release.Page);

    void OnDontShowTapped(object? sender, TappedEventArgs e) => this.DontShow.IsChecked = !this.DontShow.IsChecked;

    void OnLaterClicked(object? sender, EventArgs e) => this.Close();

    async void OnInstallClicked(object? sender, EventArgs e)
    {
        this.InstallButton.IsEnabled = this.LaterButton.IsEnabled = false;
        this.Progress.Value = 0;
        this.Progress.IsVisible = true;
        this.Status.ClearValue(Label.TextColorProperty);
        this.Status.Text = "Downloading…";
        this.Status.IsVisible = true;
        try
        {
            await this.updates.InstallAsync(new Progress<double>(p =>
            {
                this.Progress.Value = p;
                if (p >= 1)
                    this.Status.Text = "Installing…";
            }));
            // installed: the app is quitting to come back as the new version
        }
        catch (Exception ex)
        {
            this.Progress.IsVisible = false;
            this.Status.TextColor = Colors.IndianRed;
            this.Status.Text = $"Couldn't install the update: {ex.Message}";
            this.InstallButton.IsEnabled = this.LaterButton.IsEnabled = true;
        }
    }

    void Close()
    {
        if (this.Window is { } window)
            Application.Current?.CloseWindow(window);
    }
}
