using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using LlmodelBox.Common;

namespace LlmodelBox.Services
{
    public class LogoService : ILogoService
    {
        private readonly HttpClient _httpClient;
        private readonly string _cacheDir;

        public LogoService(HttpClient? httpClient = null, string? cacheDir = null)
        {
            _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            _cacheDir = cacheDir ?? AppConstants.CacheDir;
            if (!Directory.Exists(_cacheDir))
            {
                Directory.CreateDirectory(_cacheDir);
            }
        }

        public string ExtractDomain(string urlOrDomain)
        {
            if (string.IsNullOrWhiteSpace(urlOrDomain)) return string.Empty;
            string clean = urlOrDomain.Trim().ToLowerInvariant();

            if (!clean.StartsWith("http://") && !clean.StartsWith("https://"))
            {
                clean = "https://" + clean;
            }

            try
            {
                var uri = new Uri(clean);
                string host = uri.Host;
                if (host.StartsWith("www.")) host = host.Substring(4);
                return host;
            }
            catch
            {
                return clean.Replace("https://", "").Replace("http://", "").Split('/')[0];
            }
        }

        // WPF's BitmapImage can decode PNG/JPEG/GIF/BMP/ICO/WEBP but not SVG or HTML error pages.
        // Reject anything that isn't a known raster so a bad source never gets cached as a broken icon.
        private static bool IsDecodableRaster(byte[] b)
        {
            if (b.Length < 4) return false;
            // PNG
            if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return true;
            // JPEG
            if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return true;
            // GIF
            if (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return true;
            // BMP
            if (b[0] == 0x42 && b[1] == 0x4D) return true;
            // ICO / CUR (reserved=0, type=1 or 2)
            if (b[0] == 0x00 && b[1] == 0x00 && (b[2] == 0x01 || b[2] == 0x02) && b[3] == 0x00) return true;
            // RIFF....WEBP
            if (b.Length >= 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 &&
                b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return true;
            return false;
        }

        public string GetCachedLogoPath(string domain)
        {
            if (string.IsNullOrWhiteSpace(domain)) return string.Empty;
            string cleanDomain = ExtractDomain(domain);
            string path = Path.Combine(_cacheDir, $"{cleanDomain}.png");
            return File.Exists(path) && new FileInfo(path).Length > 200 ? path : string.Empty;
        }

        public async Task<string> FetchAndCacheLogoAsync(string urlOrDomain)
        {
            string domain = ExtractDomain(urlOrDomain);
            if (string.IsNullOrWhiteSpace(domain)) return string.Empty;

            try
            {
                string localPath = Path.Combine(_cacheDir, $"{domain}.png");
                if (File.Exists(localPath))
                {
                    var fileInfo = new FileInfo(localPath);
                    if (fileInfo.Length > 200)
                    {
                        return localPath;
                    }
                }

                // Ordered sources, best brand-logo quality first. Clearbit's free logo API was shut
                // down, so we use unavatar (real brand logos, no key) then Google's favicon CDN which
                // always returns a decodable PNG. Each candidate is validated to be a raster image WPF
                // can actually decode — SVG/HTML responses are rejected so we never cache a broken icon.
                string[] sources =
                {
                    $"https://unavatar.io/{domain}?fallback=false",
                    $"https://www.google.com/s2/favicons?domain={domain}&sz=128",
                    $"https://icons.duckduckgo.com/ip3/{domain}.ico"
                };

                foreach (var src in sources)
                {
                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get, src);
                        req.Headers.Add("User-Agent", "LlmodelBox/1.0 (Windows NT 10.0; Win64; x64)");
                        var resp = await _httpClient.SendAsync(req);
                        if (!resp.IsSuccessStatusCode) continue;
                        byte[] bytes = await resp.Content.ReadAsByteArrayAsync();
                        if (bytes.Length > 200 && IsDecodableRaster(bytes))
                        {
                            await File.WriteAllBytesAsync(localPath, bytes);
                            return localPath;
                        }
                    }
                    catch { /* try next source */ }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error fetching logo for {domain}: {ex.Message}");
            }

            return string.Empty;
        }
    }
}
