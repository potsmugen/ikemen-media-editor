using System.Net;
using System.Text.Json;
using Avalonia.Interactivity;

namespace Editor;

public partial class MainWindow
{
    private const string RepoUrl = "https://github.com/potsmugen/ikemen-media-editor";
    private const string RepoApi = "https://api.github.com/repos/potsmugen/ikemen-media-editor";
    private bool _checkingUpdates;

    /// <summary>"date+commit" from a CI build, or the date alone from a local build.</summary>
    private static string BuildVersion =>
        System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(MainWindow).Assembly)
            ?.InformationalVersion ?? "unknown";

    // Manual only, so startup never waits on the network
    private async void OnCheckForUpdates(object? sender, RoutedEventArgs e)
    {
        if (_checkingUpdates) return;
        _checkingUpdates = true;
        try
        {
            const string title = "Check for Updates";
            string message;
            string? tag;
            try
            {
                (message, tag) = await CheckForUpdates();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                           or KeyNotFoundException or InvalidOperationException)
            {
                CrashLog.Error("OnCheckForUpdates", ex.ToString());
                bool limited = ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests };
                await Dialogs.ShowMessage(this, title, limited
                    ? "GitHub is limiting requests right now. Try again later."
                    : "Could not check for updates. Check your internet connection and try again.");
                return;
            }

            if (tag is null)
            {
                await Dialogs.ShowMessage(this, title, message);
                return;
            }
            if (await Dialogs.ShowMessageWithAction(this, title, message, "Open Release Page"))
            {
                string url = $"{RepoUrl}/releases/tag/{Uri.EscapeDataString(tag)}";
                if (!await Launcher.LaunchUriAsync(new Uri(url)))
                    CrashLog.Error("OnCheckForUpdates", $"could not open {url}");
            }
        }
        catch (Exception ex)
        {
            await Failed("OnCheckForUpdates", ex);
        }
        finally
        {
            _checkingUpdates = false;
        }
    }

    /// <summary>The message to show, and the release tag to offer when there is one worth opening.</summary>
    private async Task<(string Message, string? Tag)> CheckForUpdates()
    {
        var g = _settings.General;
        int timeout = Settings.Checked(nameof(g.UpdateCheckTimeoutSeconds), g.UpdateCheckTimeoutSeconds,
            Settings.DefaultUpdateCheckTimeoutSeconds, v => v >= 1);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeout) };
        // GitHub refuses API requests without a User-Agent
        http.DefaultRequestHeaders.UserAgent.ParseAdd("IkemenMediaEditor");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        string tag = await GetJsonString(http, RepoApi + "/releases/latest", "tag_name");
        if (!DateTime.TryParseExact(tag, "yyyy.MM.dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _))
            throw new InvalidOperationException($"unexpected release tag {tag}");

        string build = BuildVersion;
        string[] parts = build.Split('+', 2);
        string date = parts[0];
        // A local build has no commit, so it can't be placed against a release
        if (parts.Length < 2 || parts[1].Length == 0)
            return ($"This is a development build ({build}).\nThe latest release is {tag}.", tag);
        string commit = parts[1];

        // Tags are YYYY.MM.DD, so text order is date order
        int order = string.CompareOrdinal(tag, date);
        bool newer = order > 0;
        // A same-day build replaces that day's release, so the commit decides
        if (order == 0)
        {
            string sha = await GetJsonString(http, $"{RepoApi}/commits/{Uri.EscapeDataString(tag)}", "sha");
            newer = !sha.StartsWith(commit, StringComparison.OrdinalIgnoreCase);
        }
        return newer
            ? ($"A newer build is available: {tag}.\nYou have {build}.", tag)
            : ($"You have the latest build ({build}).", null);
    }

    private static async Task<string> GetJsonString(HttpClient http, string url, string property)
    {
        using var response = await http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        // JsonDocument instead of typed deserializing, which trimming can break
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty(property).GetString()
            ?? throw new InvalidOperationException($"{url}: {property} is empty");
    }
}
