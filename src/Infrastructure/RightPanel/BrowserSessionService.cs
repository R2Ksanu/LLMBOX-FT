using System;
using System.Collections.Generic;
using System.Linq;
using LlmodelBox.Common;

namespace LlmodelBox.Infrastructure.RightPanel
{
    public sealed class BrowserTabState
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = "New Tab";
        public string Url { get; set; } = string.Empty;
        public List<string> History { get; } = new();
        public int HistoryIndex { get; set; } = -1;
        public bool CanGoBack => HistoryIndex > 0;
        public bool CanGoForward => HistoryIndex >= 0 && HistoryIndex < History.Count - 1;
    }

    public sealed class BrowserHistoryEntry
    {
        public DateTime VisitedUtc { get; init; } = DateTime.UtcNow;
        public string Title { get; init; } = string.Empty;
        public string Url { get; init; } = string.Empty;
    }

    public sealed class BrowserDownloadEntry
    {
        public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
        public string FileName { get; init; } = string.Empty;
        public string Url { get; init; } = string.Empty;
        public string TargetPath { get; init; } = string.Empty;
        public string Status { get; set; } = "Downloading";
    }

    /// <summary>
    /// Tracks embedded WebView2 browser tabs, navigation history, downloads, home URL,
    /// and separate permissions for interactive downloads vs AI browser automation.
    /// </summary>
    public sealed class BrowserSessionService
    {
        private readonly List<BrowserTabState> _tabs = new();
        private readonly List<BrowserHistoryEntry> _globalHistory = new();
        private readonly List<BrowserDownloadEntry> _downloads = new();

        public string HomeUrl { get; set; } = "https://learn.microsoft.com";
        public bool AllowDownloads { get; set; } = true;
        public bool AllowAiAutomation { get; set; } = false;

        public IReadOnlyList<BrowserTabState> Tabs => _tabs;
        public IReadOnlyList<BrowserHistoryEntry> History => _globalHistory;
        public IReadOnlyList<BrowserDownloadEntry> Downloads => _downloads;

        public BrowserTabState OpenTab(string? url = null)
        {
            string target = NormalizeInputToUrl(string.IsNullOrWhiteSpace(url) ? HomeUrl : url);
            var tab = new BrowserTabState { Url = target, Title = ExtractHostOrTitle(target) };
            RecordNavigation(tab, target, tab.Title);
            _tabs.Add(tab);
            return tab;
        }

        public bool CloseTab(string tabId)
        {
            var t = _tabs.FirstOrDefault(x => string.Equals(x.Id, tabId, StringComparison.Ordinal));
            if (t == null || _tabs.Count <= 1) return false;
            _tabs.Remove(t);
            return true;
        }

        public void RecordNavigation(BrowserTabState tab, string url, string? title = null)
        {
            if (tab == null || string.IsNullOrWhiteSpace(url)) return;
            tab.Url = url;
            tab.Title = string.IsNullOrWhiteSpace(title) ? ExtractHostOrTitle(url) : title;

            if (tab.HistoryIndex < tab.History.Count - 1)
            {
                tab.History.RemoveRange(tab.HistoryIndex + 1, tab.History.Count - tab.HistoryIndex - 1);
            }
            if (tab.History.Count == 0 || !string.Equals(tab.History[^1], url, StringComparison.OrdinalIgnoreCase))
            {
                tab.History.Add(url);
                tab.HistoryIndex = tab.History.Count - 1;
            }

            _globalHistory.Insert(0, new BrowserHistoryEntry { Url = url, Title = tab.Title });
            if (_globalHistory.Count > 100) _globalHistory.RemoveAt(_globalHistory.Count - 1);
        }

        public string? GoBack(BrowserTabState tab)
        {
            if (tab == null || !tab.CanGoBack) return null;
            tab.HistoryIndex--;
            tab.Url = tab.History[tab.HistoryIndex];
            return tab.Url;
        }

        public string? GoForward(BrowserTabState tab)
        {
            if (tab == null || !tab.CanGoForward) return null;
            tab.HistoryIndex++;
            tab.Url = tab.History[tab.HistoryIndex];
            return tab.Url;
        }

        public void AddDownload(BrowserDownloadEntry entry)
        {
            _downloads.Insert(0, entry);
            if (_downloads.Count > 50) _downloads.RemoveAt(_downloads.Count - 1);
        }

        public bool CanAiAutomate(out string reason)
        {
            if (!AllowAiAutomation)
            {
                reason = "AI Browser Automation is disabled in Browser Settings.";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        public static string NormalizeInputToUrl(string input)
        {
            string trimmed = (input ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(trimmed)) return "https://learn.microsoft.com";

            if (trimmed.Contains("://", StringComparison.Ordinal))
            {
                return WebLink.TryParse(trimmed, out var safe) && safe != null
                    ? safe.AbsoluteUri
                    : "https://learn.microsoft.com/en-us/search/?terms=" + Uri.EscapeDataString(trimmed);
            }

            if (trimmed.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase))
            {
                return "http://" + trimmed;
            }

            if (trimmed.Contains('.') && !trimmed.Contains(' '))
            {
                string withHttps = "https://" + trimmed;
                if (WebLink.TryParse(withHttps, out var hostUri) && hostUri != null)
                    return hostUri.AbsoluteUri;
            }

            return "https://learn.microsoft.com/en-us/search/?terms=" + Uri.EscapeDataString(trimmed);
        }

        private static string ExtractHostOrTitle(string url)
        {
            if (WebLink.TryParse(url, out var uri) && uri != null) return uri.Host;
            return "Tab";
        }
    }
}
