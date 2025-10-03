using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Polly;
using Polly.Retry;

public static class ImageSuperResolver
{
    private static readonly string[] ValidExtensions = [".svg", ".png", ".jpg", ".jpeg"]; // keep in sync with ImageValidator
    private static readonly string[] ImageMimeTypes = ["image/svg+xml","image/png","image/jpeg","image/jpg","image/webp"]; // accept webp then fall back to png

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly AsyncRetryPolicy<HttpResponseMessage> RetryPolicy = Policy
        .HandleResult<HttpResponseMessage>(r => (int)r.StatusCode >= 500 || r.StatusCode == HttpStatusCode.TooManyRequests)
        .Or<HttpRequestException>()
        .WaitAndRetryAsync(3, attempt => TimeSpan.FromMilliseconds(200 * attempt));

    public class ResolveResult
    {
        public bool Success { get; set; }
        public string? FinalUrl { get; set; }
        public string? SourceUrl { get; set; } // the page or original url we started from
        public string? Reason { get; set; }
    }

    public static async Task<ResolveResult> ResolveAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new ResolveResult { Success = false, Reason = "Empty URL" };

        // If it's a direct url with acceptable extension, validate via ImageValidator
        if (HasValidExtension(url))
        {
            if (await ImageValidator.IsValidImageUrl(url))
                return new ResolveResult { Success = true, FinalUrl = url, SourceUrl = url };
            // try the wayback machine if the direct url fails
            var archived = await TryWaybackAsync(url);
            if (archived.Success) return archived;
            return new ResolveResult { Success = false, SourceUrl = url, Reason = "Direct URL invalid and no archived copy found" };
        }

        // Not a direct image: try to resolve from HTML content
        var fromHtml = await TryExtractImageFromPageAsync(url);
        if (fromHtml.Success)
            return fromHtml;

        // If the page itself failed or no images found, try Wayback for the page and repeat extraction
        var archivedPage = await TryWaybackAsync(url);
        if (archivedPage.Success && archivedPage.FinalUrl != null)
        {
            var extractedFromArchive = await TryExtractImageFromPageAsync(archivedPage.FinalUrl);
            if (extractedFromArchive.Success)
                return extractedFromArchive;
        }

        return new ResolveResult { Success = false, SourceUrl = url, Reason = "Could not resolve an image from page or archive" };
    }

    private static bool HasValidExtension(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return !string.IsNullOrEmpty(ext) && ValidExtensions.Contains(ext);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<ResolveResult> TryExtractImageFromPageAsync(string pageUrl)
    {
        try
        {
            Console.Error.WriteLine($"🌐 Fetching page: {pageUrl}");
            var response = await RetryPolicy.ExecuteAsync(() => Http.GetAsync(pageUrl));
            if (!response.IsSuccessStatusCode)
            {
                return new ResolveResult { Success = false, SourceUrl = pageUrl, Reason = $"HTTP {(int)response.StatusCode}" };
            }

            var baseUri = new Uri(pageUrl);
            var html = await response.Content.ReadAsStringAsync();
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Strategy 1: <meta property="og:image" content="...">
            var metaOg = doc.DocumentNode.SelectSingleNode("//meta[@property='og:image' or @name='og:image']" )
                         ?? doc.DocumentNode.SelectSingleNode("//meta[@property='twitter:image' or @name='twitter:image']");
            var candidateUrls = new List<string>();
            if (metaOg != null)
            {
                var content = metaOg.GetAttributeValue("content", null);
                if (!string.IsNullOrWhiteSpace(content)) candidateUrls.Add(MakeAbsolute(baseUri, content));
            }

            // Strategy 2: <link rel="image_src" href="...">
            foreach (var link in doc.DocumentNode.SelectNodes("//link[@rel='image_src' or contains(@rel,'icon') or contains(@rel,'apple-touch-icon')]") ?? Enumerable.Empty<HtmlNode>())
            {
                var href = link.GetAttributeValue("href", null);
                if (!string.IsNullOrWhiteSpace(href)) candidateUrls.Add(MakeAbsolute(baseUri, href));
            }

            // Strategy 3: all <img> tags
            foreach (var img in doc.DocumentNode.SelectNodes("//img[@src]") ?? Enumerable.Empty<HtmlNode>())
            {
                var src = img.GetAttributeValue("src", null);
                if (!string.IsNullOrWhiteSpace(src)) candidateUrls.Add(MakeAbsolute(baseUri, src));
                var srcset = img.GetAttributeValue("srcset", null);
                if (!string.IsNullOrWhiteSpace(srcset))
                {
                    // pick the first candidate from srcset
                    var first = srcset.Split(',').Select(s => s.Trim().Split(' ')[0]).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(first)) candidateUrls.Add(MakeAbsolute(baseUri, first));
                }
            }

            // Strategy 4: sniff for JSON-LD images
            foreach (var script in doc.DocumentNode.SelectNodes("//script[@type='application/ld+json']") ?? Enumerable.Empty<HtmlNode>())
            {
                try
                {
                    var json = script.InnerText;
                    // very lightweight find of http(s) urls ending with an image extension
                    foreach (Match m in Regex.Matches(json, @"https?://[^""']+\.(svg|png|jpg|jpeg)", RegexOptions.IgnoreCase))
                    {
                        candidateUrls.Add(m.Value);
                    }
                }
                catch { /* ignore malformed json */ }
            }

            // de-dup and prioritize by extension preference (png,jpg,svg)
            var ordered = candidateUrls
                .Where(u => Uri.IsWellFormedUriString(u, UriKind.Absolute))
                .Distinct()
                .OrderBy(u => PreferenceScore(u))
                .ToList();

            foreach (var candidate in ordered)
            {
                // If no extension but Content-Type is image, accept and append a best-guess extension
                var validated = await ValidateOrInferAsync(candidate);
                if (validated.Success)
                {
                    return new ResolveResult { Success = true, FinalUrl = validated.FinalUrl, SourceUrl = pageUrl };
                }
            }

            return new ResolveResult { Success = false, SourceUrl = pageUrl, Reason = "No valid image candidates found" };
        }
        catch (Exception ex)
        {
            return new ResolveResult { Success = false, SourceUrl = pageUrl, Reason = ex.Message };
        }
    }

    private static int PreferenceScore(string url)
    {
        var ext = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
        return ext switch
        {
            ".png" => 0,
            ".jpg" => 1,
            ".jpeg" => 1,
            ".svg" => 2,
            _ => 3
        };
    }

    private static async Task<ResolveResult> ValidateOrInferAsync(string candidateUrl)
    {
        // If the url already looks valid, delegate to ImageValidator
        if (HasValidExtension(candidateUrl))
        {
            if (await ImageValidator.IsValidImageUrl(candidateUrl))
                return new ResolveResult { Success = true, FinalUrl = candidateUrl };

            // Try archive
            return await TryWaybackAsync(candidateUrl);
        }

        // Attempt HEAD/GET to detect image content-type and validate bytes directly
        using var headReq = new HttpRequestMessage(HttpMethod.Head, candidateUrl);
        var headResp = await RetryPolicy.ExecuteAsync(() => Http.SendAsync(headReq));
        string? contentType = headResp.Content?.Headers?.ContentType?.MediaType;

        if (contentType == null || !ImageMimeTypes.Contains(contentType))
        {
            // Some servers block HEAD; try GET
            var getResp = await RetryPolicy.ExecuteAsync(() => Http.GetAsync(candidateUrl));
            if (!getResp.IsSuccessStatusCode)
                return new ResolveResult { Success = false, Reason = $"Fetch failed {(int)getResp.StatusCode}" };
            contentType = getResp.Content.Headers.ContentType?.MediaType;
            if (contentType == null || !ImageMimeTypes.Contains(contentType))
                return new ResolveResult { Success = false, Reason = "Not an image content-type" };

            // Validate signature from bytes
            var bytes = await getResp.Content.ReadAsByteArrayAsync();
            if (ValidateBytes(bytes, contentType))
                return new ResolveResult { Success = true, FinalUrl = candidateUrl };

            return new ResolveResult { Success = false, Reason = "Content did not match image signature" };
        }

        // Content-Type indicates image but HEAD didn't include body; attempt GET for validation
        var getForSig = await RetryPolicy.ExecuteAsync(() => Http.GetAsync(candidateUrl));
        if (!getForSig.IsSuccessStatusCode)
            return new ResolveResult { Success = false, Reason = $"GET failed {(int)getForSig.StatusCode}" };
        var bytes2 = await getForSig.Content.ReadAsByteArrayAsync();
        if (ValidateBytes(bytes2, contentType))
            return new ResolveResult { Success = true, FinalUrl = candidateUrl };
        return new ResolveResult { Success = false, Reason = "Content did not match image signature" };
    }

    private static string AppendExtensionHint(string url, string ext)
    {
        try
        {
            var uri = new Uri(url);
            if (!string.IsNullOrEmpty(Path.GetExtension(uri.AbsolutePath))) return url; // already has
            var builder = new UriBuilder(uri);
            var q = string.IsNullOrEmpty(builder.Query) ? $"ext={ext.Trim('.')}" : builder.Query.TrimStart('?') + $"&ext={ext.Trim('.')}";
            builder.Query = q;
            return builder.Uri.ToString();
        }
        catch
        {
            return url;
        }
    }

    private static async Task<ImageSuperResolver.ResolveResult> TryWaybackAsync(string originalUrl)
    {
        try
        {
            // Use Wayback Machine Availability API
            var api = $"https://archive.org/wayback/available?url={Uri.EscapeDataString(originalUrl)}";
            var resp = await RetryPolicy.ExecuteAsync(() => Http.GetAsync(api));
            if (!resp.IsSuccessStatusCode)
                return new ResolveResult { Success = false, SourceUrl = originalUrl, Reason = $"Archive API {(int)resp.StatusCode}" };

            var json = await resp.Content.ReadAsStringAsync();
            var snapshot = System.Text.Json.JsonDocument.Parse(json).RootElement
                .GetProperty("archived_snapshots");
            if (snapshot.TryGetProperty("closest", out var closest) && closest.TryGetProperty("url", out var archivedUrlEl))
            {
                var archivedUrl = archivedUrlEl.GetString();
                if (!string.IsNullOrWhiteSpace(archivedUrl))
                {
                    // If it's a page, we may need to extract again; if it's an image, validate directly
                    if (HasValidExtension(archivedUrl))
                    {
                        if (await ImageValidator.IsValidImageUrl(archivedUrl))
                            return new ResolveResult { Success = true, FinalUrl = archivedUrl, SourceUrl = originalUrl };
                    }
                    else
                    {
                        return new ResolveResult { Success = true, FinalUrl = archivedUrl, SourceUrl = originalUrl };
                    }
                }
            }
            return new ResolveResult { Success = false, SourceUrl = originalUrl, Reason = "No archive snapshot found" };
        }
        catch (Exception ex)
        {
            return new ResolveResult { Success = false, SourceUrl = originalUrl, Reason = ex.Message };
        }
    }

    private static string MakeAbsolute(Uri baseUri, string href)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(href)) return href;
            if (Uri.IsWellFormedUriString(href, UriKind.Absolute)) return href;
            return new Uri(baseUri, href).ToString();
        }
        catch
        {
            return href;
        }
    }

    private static bool ValidateBytes(byte[] bytes, string contentType)
    {
        if (bytes == null || bytes.Length < 8) return false;
        if (contentType.Contains("png"))
        {
            var sig = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            return bytes.Take(8).SequenceEqual(sig);
        }
        if (contentType.Contains("jpeg") || contentType.Contains("jpg"))
        {
            // FF D8 ... FF D9
            return bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[^2] == 0xFF && bytes[^1] == 0xD9;
        }
        if (contentType.Contains("svg"))
        {
            try
            {
                var text = System.Text.Encoding.UTF8.GetString(bytes).ToLowerInvariant();
                return text.Contains("<svg") && text.Contains("http://www.w3.org/2000/svg");
            }
            catch { return false; }
        }
        // Loosely accept webp if encountered
        if (contentType.Contains("webp"))
        {
            // RIFF....WEBP header
            return bytes.Length > 12 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' && bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P';
        }
        return false;
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Mozilla", "5.0"));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("(Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/125 Safari/537.36");
        http.Timeout = TimeSpan.FromSeconds(20);
        return http;
    }
}
