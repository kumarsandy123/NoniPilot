using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NoniPilot.Desktop.Navigation;
using NoniPilot.Desktop.Services;
using NoniPilot.Domain.Models;

namespace NoniPilot.Desktop.Pages;

public partial class BrowserAutomationPage : UserControl, INavigablePage
{
    private readonly AppServices _services;
    private List<BrowserBookmark> _bookmarks;

    private sealed record RecentRow(string TimeLabel, string Url);

    public BrowserAutomationPage(AppServices services)
    {
        InitializeComponent();
        _services = services;
        _bookmarks = BrowserBookmarkStore.Load();
        BookmarksList.ItemsSource = _bookmarks;
    }

    public void OnNavigatedTo() => _ = RefreshRecentAsync();

    public void OnNavigatedFrom() { }

    private async Task RefreshRecentAsync()
    {
        var events = await _services.Audit.QueryAsync(limit: 200);
        RecentList.ItemsSource = events
            .Where(e => e.Action == "Browser.OpenUrl" && e.Outcome == AuditOutcome.Success)
            .Take(20)
            .Select(e => new RecentRow(e.Timestamp.ToLocalTime().ToString("g"), e.Target ?? ""))
            .ToList();
    }

    private void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Open(UrlBox.Text.Trim());
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e) => Open(UrlBox.Text.Trim());

    private void Bookmark_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is BrowserBookmark bookmark)
        {
            Open(bookmark.Url);
        }
    }

    private void Open(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        // Lets the URL box double as a "jump to bookmark" field - typing a saved bookmark's
        // label (e.g. "Gmail") opens that bookmark's URL instead of being treated as a literal
        // (and invalid) URL.
        var byLabel = _bookmarks.FirstOrDefault(b => string.Equals(b.Label, input, StringComparison.OrdinalIgnoreCase));
        var url = byLabel?.Url ?? input;

        _ = RunAsync(url);
    }

    private async Task RunAsync(string url)
    {
        var (success, message) = await _services.ActionRunner.RunAsync(
            "Browser", "OpenUrl", new Dictionary<string, object?> { ["url"] = url },
            ct => _services.Browser.OpenUrlAsync(url, ct));

        StatusText.Text = success ? $"Opened {url}" : message;
        if (success)
        {
            await RefreshRecentAsync();
        }
    }

    private void AddBookmark_Click(object sender, RoutedEventArgs e)
    {
        var label = NewBookmarkLabelBox.Text.Trim();
        var url = NewBookmarkUrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(url))
        {
            StatusText.Text = "Enter both a label and a URL.";
            return;
        }

        _bookmarks.Add(new BrowserBookmark(label, url));
        BrowserBookmarkStore.Save(_bookmarks);
        RefreshBookmarksList();
        NewBookmarkLabelBox.Clear();
        NewBookmarkUrlBox.Clear();
    }

    private void DeleteBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not BrowserBookmark bookmark)
        {
            return;
        }

        _bookmarks.Remove(bookmark);
        BrowserBookmarkStore.Save(_bookmarks);
        RefreshBookmarksList();
        StatusText.Text = $"Deleted bookmark \"{bookmark.Label}\".";
    }

    private void RefreshBookmarksList()
    {
        BookmarksList.ItemsSource = null;
        BookmarksList.ItemsSource = _bookmarks;
    }
}
