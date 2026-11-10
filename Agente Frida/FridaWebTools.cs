using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace FridaAgent
{
    public sealed class FridaSearchResultItem
    {
        public string Title { get; set; }
        public string Url { get; set; }
        public string Snippet { get; set; }

        public FridaSearchResultItem() { }

        public FridaSearchResultItem(string title, string url, string snippet)
        {
            Title = title ?? "";
            Url = url ?? "";
            Snippet = snippet ?? "";
        }
    }

    public static class FridaWebTools
    {
        private static readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };

        public static bool IsSafeUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            string u = url.Trim();
            if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return false;

            try
            {
                var uri = new Uri(u);
                string host = uri.Host.ToLowerInvariant();
                if (host == "localhost" || host == "127.0.0.1" || host == "::1") return false;
                if (host.StartsWith("192.168.") || host.StartsWith("10.") || host.StartsWith("169.254.")) return false;
                if (Regex.IsMatch(host, @"^172\.(1[6-9]|2[0-9]|3[0-1])\.")) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static string HtmlToPlainText(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return "";

            // Remove script and style tags and contents
            string text = Regex.Replace(html, @"<script[^>]*>[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<style[^>]*>[\s\S]*?</style>", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<svg[^>]*>[\s\S]*?</svg>", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<noscript[^>]*>[\s\S]*?</noscript>", "", RegexOptions.IgnoreCase);

            // Extract title if present
            string title = "";
            Match titleMatch = Regex.Match(html, @"<title[^>]*>([\s\S]*?)</title>", RegexOptions.IgnoreCase);
            if (titleMatch.Success)
            {
                title = WebUtility.HtmlDecode(titleMatch.Groups[1].Value.Trim());
            }

            // Convert common block elements to linebreaks
            text = Regex.Replace(text, @"<(?:p|div|h[1-6]|li|tr|br|section|article)[^>]*>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<[^>]+>", " ");

            // Decode HTML entities
            text = WebUtility.HtmlDecode(text);

            // Normalize spaces and linebreaks
            text = Regex.Replace(text, @"[ \t]+", " ");
            text = Regex.Replace(text, @"\n\s*\n+", "\n\n");
            text = text.Trim();

            if (!string.IsNullOrEmpty(title))
            {
                text = "Título: " + title + "\n\n" + text;
            }

            return text;
        }

        public static async Task<FridaToolReceipt> FetchUrlAsync(string url, HttpClient http, int maxChars = 32000, int timeoutSeconds = 15)
        {
            string cleanUrl = (url ?? "").Trim();
            var receipt = new FridaToolReceipt("fetch_url", "web_page_read", cleanUrl, false);

            if (!IsSafeUrl(cleanUrl))
            {
                return receipt.Fail("unsafe_url", "URL invalida ou restrita: " + cleanUrl);
            }

            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
                using (var req = new HttpRequestMessage(HttpMethod.Get, cleanUrl))
                {
                    req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 FridaAgent/2.0");
                    req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,text/plain;q=0.8,*/*;q=0.5");

                    using (var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                    {
                        receipt.Put("status_code", (int)resp.StatusCode);
                        if (!resp.IsSuccessStatusCode)
                        {
                            return receipt.Fail("http_error", "HTTP " + (int)resp.StatusCode + " " + resp.ReasonPhrase);
                        }

                        string raw = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        byte[] rawBytes = Encoding.UTF8.GetBytes(raw);
                        string sha256;
                        using (var sha = SHA256.Create())
                        {
                            sha256 = BitConverter.ToString(sha.ComputeHash(rawBytes)).Replace("-", "").ToLowerInvariant();
                        }

                        string plain = HtmlToPlainText(raw);
                        if (plain.Length > maxChars)
                        {
                            plain = plain.Substring(0, maxChars) + "\n\n[... conteudo truncado em " + maxChars + " caracteres ...]";
                        }

                        receipt.Put("url", cleanUrl);
                        receipt.Put("sha256", sha256);
                        receipt.Put("content_length", raw.Length);
                        receipt.Put("plain_length", plain.Length);
                        receipt.Put("untrusted_output", plain);

                        receipt.Check("http_success", (int)resp.StatusCode == 200, "200", ((int)resp.StatusCode).ToString(), "web_server");
                        receipt.Check("content_received", plain.Length > 0, "> 0", plain.Length.ToString(), "html_parser");

                        return receipt.Finish(FridaResultStatus.Verified, "page_fetched", "Pagina carregada com sucesso (" + plain.Length + " caracteres extraidos).", plain);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return receipt.Fail("timeout", "Tempo esgotado ao acessar a URL: " + cleanUrl);
            }
            catch (Exception ex)
            {
                return receipt.Fail("fetch_exception", "Falha de rede: " + ex.Message);
            }
        }

        public static async Task<FridaToolReceipt> WebSearchAsync(string query, HttpClient http, string apiKey, FridaSettings settings, int maxResults = 5)
        {
            string cleanQuery = (query ?? "").Trim();
            var receipt = new FridaToolReceipt("web_search", "search_results", cleanQuery, false);

            if (!FridaTaskRouting.IsSafeWebSearchQuery(cleanQuery))
            {
                return receipt.Fail("unsafe_query", "Consulta de busca rejeitada por conter caminhos locais ou termos confidenciais.");
            }

            var results = new List<FridaSearchResultItem>();

            // Method 1: Google Search via Gemini API if key is present
            string key = (apiKey ?? (settings != null ? settings.api_key : "")).Split(new char[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (!string.IsNullOrEmpty(key))
            {
                try
                {
                    string model = ModelSelection.GeminiLite;
                    var payload = new Dictionary<string, object>
                    {
                        { "model", model },
                        { "store", false },
                        { "input", FridaInteractionPolicy.LocalDateContext() + "Liste resultados com titulo, url e trecho relevante para: " + cleanQuery },
                        { "tools", new object[] { new Dictionary<string, object> { { "type", "google_search" } } } }
                    };

                    using (var req = new HttpRequestMessage(HttpMethod.Post, "https://generativelanguage.googleapis.com/v1beta/interactions"))
                    {
                        req.Headers.Add("x-goog-api-key", key);
                        req.Content = new StringContent(_json.Serialize(payload), Encoding.UTF8, "application/json");

                        using (var resp = await http.SendAsync(req).ConfigureAwait(false))
                        {
                            if (resp.IsSuccessStatusCode)
                            {
                                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                                var root = _json.Deserialize<Dictionary<string, object>>(body);
                                List<string> citations = FridaInteractionPolicy.Citations(root);

                                if (citations != null && citations.Count > 0)
                                {
                                    foreach (string c in citations.Take(maxResults))
                                    {
                                        string u = c.Trim();
                                        results.Add(new FridaSearchResultItem(u, u, "Fonte consultada via Google Search"));
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    BootLogger.Log("WebSearch Gemini fallback: " + ex.Message);
                }
            }

            // Fallback: DuckDuckGo / SearX / RSS if direct citations empty
            if (results.Count == 0)
            {
                try
                {
                    string searchUrl = "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(cleanQuery);
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    using (var req = new HttpRequestMessage(HttpMethod.Get, searchUrl))
                    {
                        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                        using (var resp = await http.SendAsync(req, cts.Token).ConfigureAwait(false))
                        {
                            if (resp.IsSuccessStatusCode)
                            {
                                string html = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                                var matches = Regex.Matches(html, @"<a[^>]+class=""result__url""[^>]+href=""([^""]+)""[^>]*>([\s\S]*?)</a>", RegexOptions.IgnoreCase);
                                foreach (Match m in matches)
                                {
                                    string rawUrl = m.Groups[1].Value.Trim();
                                    string title = WebUtility.HtmlDecode(Regex.Replace(m.Groups[2].Value, @"<[^>]+>", "").Trim());
                                    if (rawUrl.Contains("uddg="))
                                    {
                                        Match uddg = Regex.Match(rawUrl, @"uddg=([^&]+)");
                                        if (uddg.Success) rawUrl = Uri.UnescapeDataString(uddg.Groups[1].Value);
                                    }
                                    if (IsSafeUrl(rawUrl))
                                    {
                                        results.Add(new FridaSearchResultItem(title, rawUrl, ""));
                                        if (results.Count >= maxResults) break;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    BootLogger.Log("WebSearch DDG fallback: " + ex.Message);
                }
            }

            if (results.Count == 0)
            {
                return receipt.Fail("no_results", "Nenhum resultado de busca encontrado para: " + cleanQuery);
            }

            receipt.Put("query", cleanQuery);
            receipt.Put("results_count", results.Count);
            receipt.Put("results", results.Select(r => new { r.Title, r.Url, r.Snippet }).ToArray());
            receipt.Check("results_found", results.Count > 0, "> 0", results.Count.ToString(), "search_engine");

            var sb = new StringBuilder();
            sb.AppendLine("Resultados da busca para: \"" + cleanQuery + "\":");
            for (int i = 0; i < results.Count; i++)
            {
                sb.AppendLine((i + 1) + ". " + results[i].Title);
                sb.AppendLine("   URL: " + results[i].Url);
                if (!string.IsNullOrWhiteSpace(results[i].Snippet))
                    sb.AppendLine("   Trecho: " + results[i].Snippet);
            }

            return receipt.Finish(FridaResultStatus.Verified, "search_completed", sb.ToString(), _json.Serialize(results));
        }
    }
}
