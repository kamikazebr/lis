using System.ComponentModel;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;

using Lis.Core.Util;

using Microsoft.SemanticKernel;

namespace Lis.Tools;

public sealed partial class WebPlugin(IHttpClientFactory httpClientFactory) {

	[KernelFunction("search")]
	[Description("Search the web using a query string.")]
	[ToolSummarization(SummarizationPolicy.Summarize)]
	[ToolAuthorization(ToolAuthLevel.Open)]
	public async Task<string> SearchAsync(
		[Description("Search query")] string query,
		[Description("Maximum number of results (1-10)")] int maxResults = 5) {
		await ToolContext.NotifyAsync($"🔍 Searching: {query}");

		maxResults = Math.Clamp(maxResults, 1, 10);

		string? enabled = Environment.GetEnvironmentVariable("LIS_WEB_SEARCH_ENABLED");
		if (!string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))
			return "Web search is not enabled.";

		string provider = (Environment.GetEnvironmentVariable("LIS_WEB_SEARCH_PROVIDER") ?? "brave")
			.Trim().ToLowerInvariant();

		try {
			return provider switch {
				"tavily"  => await this.SearchTavilyAsync(query, maxResults),
				"serper"  => await this.SearchSerperAsync(query, maxResults),
				"searxng" => await this.SearchSearxngAsync(query, maxResults),
				_         => await this.SearchBraveAsync(query, maxResults),
			};
		} catch (Exception ex) {
			return $"Search error ({provider}): {ex.Message}";
		}
	}

	private async Task<string> SearchBraveAsync(string query, int maxResults) {
		string? apiKey = Environment.GetEnvironmentVariable("LIS_WEB_SEARCH_API_KEY");
		if (string.IsNullOrWhiteSpace(apiKey))
			return "Web search (brave) is not configured: LIS_WEB_SEARCH_API_KEY is empty.";

		string baseUrl      = GetBaseUrl("https://api.search.brave.com");
		string encodedQuery = HttpUtility.UrlEncode(query);
		string requestUrl   = $"{baseUrl}/res/v1/web/search?q={encodedQuery}&count={maxResults}";

		using HttpRequestMessage request = new(HttpMethod.Get, requestUrl);
		request.Headers.Add("X-Subscription-Token", apiKey);
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

		using HttpClient client = httpClientFactory.CreateClient();
		using HttpResponseMessage response = await client.SendAsync(request);
		response.EnsureSuccessStatusCode();

		string json = await response.Content.ReadAsStringAsync();
		using JsonDocument doc = JsonDocument.Parse(json);

		if (!doc.RootElement.TryGetProperty("web", out JsonElement web) ||
		    !web.TryGetProperty("results", out JsonElement results))
			return "No results found.";

		return FormatResults(results, maxResults, r => (
			GetString(r, "title"),
			GetString(r, "url"),
			GetString(r, "description")
		));
	}

	private async Task<string> SearchTavilyAsync(string query, int maxResults) {
		string? apiKey = Environment.GetEnvironmentVariable("LIS_WEB_SEARCH_API_KEY");
		if (string.IsNullOrWhiteSpace(apiKey))
			return "Web search (tavily) is not configured: LIS_WEB_SEARCH_API_KEY is empty.";

		string baseUrl = GetBaseUrl("https://api.tavily.com");

		var payload = new {
			api_key      = apiKey,
			query,
			max_results  = maxResults,
			search_depth = "basic"
		};

		using HttpClient client = httpClientFactory.CreateClient();
		using HttpResponseMessage response = await client.PostAsJsonAsync($"{baseUrl}/search", payload);
		response.EnsureSuccessStatusCode();

		string json = await response.Content.ReadAsStringAsync();
		using JsonDocument doc = JsonDocument.Parse(json);

		if (!doc.RootElement.TryGetProperty("results", out JsonElement results))
			return "No results found.";

		return FormatResults(results, maxResults, r => (
			GetString(r, "title"),
			GetString(r, "url"),
			GetString(r, "content")
		));
	}

	private async Task<string> SearchSerperAsync(string query, int maxResults) {
		string? apiKey = Environment.GetEnvironmentVariable("LIS_WEB_SEARCH_API_KEY");
		if (string.IsNullOrWhiteSpace(apiKey))
			return "Web search (serper) is not configured: LIS_WEB_SEARCH_API_KEY is empty.";

		string baseUrl = GetBaseUrl("https://google.serper.dev");

		using HttpRequestMessage request = new(HttpMethod.Post, $"{baseUrl}/search");
		request.Headers.Add("X-API-KEY", apiKey);
		request.Content = JsonContent.Create(new { q = query, num = maxResults });

		using HttpClient client = httpClientFactory.CreateClient();
		using HttpResponseMessage response = await client.SendAsync(request);
		response.EnsureSuccessStatusCode();

		string json = await response.Content.ReadAsStringAsync();
		using JsonDocument doc = JsonDocument.Parse(json);

		if (!doc.RootElement.TryGetProperty("organic", out JsonElement results))
			return "No results found.";

		return FormatResults(results, maxResults, r => (
			GetString(r, "title"),
			GetString(r, "link"),
			GetString(r, "snippet")
		));
	}

	private async Task<string> SearchSearxngAsync(string query, int maxResults) {
		// api_key is optional; self-hosted SearXNG usually has none.
		string? apiKey      = Environment.GetEnvironmentVariable("LIS_WEB_SEARCH_API_KEY");
		string  baseUrl     = GetBaseUrl("http://searxng:8080");
		string  encodedQuery = HttpUtility.UrlEncode(query);
		string  requestUrl  = $"{baseUrl}/search?q={encodedQuery}&format=json";

		using HttpRequestMessage request = new(HttpMethod.Get, requestUrl);
		if (!string.IsNullOrWhiteSpace(apiKey))
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

		using HttpClient client = httpClientFactory.CreateClient();
		using HttpResponseMessage response = await client.SendAsync(request);
		response.EnsureSuccessStatusCode();

		string json = await response.Content.ReadAsStringAsync();
		using JsonDocument doc = JsonDocument.Parse(json);

		if (!doc.RootElement.TryGetProperty("results", out JsonElement results))
			return "No results found.";

		return FormatResults(results, maxResults, r => (
			GetString(r, "title"),
			GetString(r, "url"),
			GetString(r, "content")
		));
	}

	[KernelFunction("fetch")]
	[Description("Fetch the content of a URL and return it as plain text.")]
	[ToolSummarization(SummarizationPolicy.Summarize)]
	[ToolAuthorization(ToolAuthLevel.Open)]
	public async Task<string> FetchAsync(
		[Description("URL to fetch")] string url,
		[Description("Maximum content length (1000-50000)")] int maxLength = 10000) {
		await ToolContext.NotifyAsync($"🌐 Fetching: {url}");

		maxLength = Math.Clamp(maxLength, 1000, 50000);

		try {
			using HttpRequestMessage request = new(HttpMethod.Get, url);
			request.Headers.UserAgent.ParseAdd("Lis/1.0");

			using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
			using HttpClient client = httpClientFactory.CreateClient();
			using HttpResponseMessage response = await client.SendAsync(request, cts.Token);
			response.EnsureSuccessStatusCode();

			string html = await response.Content.ReadAsStringAsync(cts.Token);

			// Basic HTML-to-text: strip tags, decode entities, collapse whitespace
			string text = StripHtmlTags().Replace(html, " ");
			text = HttpUtility.HtmlDecode(text);
			text = CollapseWhitespace().Replace(text, " ").Trim();

			if (text.Length > maxLength)
				text = text[..maxLength] + " [...truncated]";

			return text;
		} catch (Exception ex) {
			return $"Fetch error: {ex.Message}";
		}
	}

	private static string GetBaseUrl(string fallback) =>
		Environment.GetEnvironmentVariable("LIS_WEB_SEARCH_BASE_URL") is { Length: > 0 } b
			? b.TrimEnd('/')
			: fallback;

	private static string GetString(JsonElement e, string prop) =>
		e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String
			? v.GetString() ?? ""
			: "";

	private static string FormatResults(
		JsonElement arrayElement,
		int maxResults,
		Func<JsonElement, (string title, string url, string description)> extract) {
		StringBuilder sb = new();
		int index = 0;
		foreach (JsonElement r in arrayElement.EnumerateArray()) {
			if (index >= maxResults) break;
			index++;
			(string title, string url, string description) = extract(r);
			sb.AppendLine($"{index}. {title}");
			sb.AppendLine($"   {url}");
			sb.AppendLine($"   {description}");
		}
		string output = sb.ToString().TrimEnd();
		return output.Length > 0 ? output : "No results found.";
	}

	[GeneratedRegex("<[^>]+>")]
	private static partial Regex StripHtmlTags();

	[GeneratedRegex(@"\s+")]
	private static partial Regex CollapseWhitespace();
}
