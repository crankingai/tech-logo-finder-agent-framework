using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

public static class BraveSearchService
{
      private static readonly string apiKey = Environment.GetEnvironmentVariable("BRAVE_API_KEY") ?? throw new InvalidOperationException("BRAVE_API_KEY environment variable is not set.");
      private static readonly string endpoint = "https://api.search.brave.com/res/v1/web/search";

    public static async Task<string> SearchWeb(string query)
    {
        using (HttpClient client = new HttpClient())
        {
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.Add("X-Subscription-Token", apiKey);

            string requestUri = $"{endpoint}?q={Uri.EscapeDataString(query)}";

            try
            {
                HttpResponseMessage response = await client.GetAsync(requestUri);
                response.EnsureSuccessStatusCode();

                string content = await response.Content.ReadAsStringAsync();

                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                BraveSearchResponse? searchResponse = JsonSerializer.Deserialize<BraveSearchResponse>(content, jsonOptions);

                if (searchResponse?.Web?.Results != null)
                {
                    // Serialize the results back to JSON
                    return JsonSerializer.Serialize(searchResponse.Web.Results, new JsonSerializerOptions { WriteIndented = true });
                }
                else
                {
                    return JsonSerializer.Serialize(new { Message = "No results found." });
                }
            }
            catch (HttpRequestException e)
            {
                return JsonSerializer.Serialize(new { Error = "Request error", Message = e.Message });
            }
            catch (JsonException e)
            {
                return JsonSerializer.Serialize(new { Error = "JSON parse error", Message = e.Message });
            }
        }
    }

    public class BraveSearchResponse
    {
        [JsonPropertyName("web")]
        public WebSection? Web { get; set; }
    }

    public class WebSection
    {
        [JsonPropertyName("results")]
        public Result[]? Results { get; set; }
    }

    public class Result
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }
    }
}
