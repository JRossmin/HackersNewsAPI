using HackersNewsAPI.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HackersNewsAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class HackerNewsController : ControllerBase
    {
        private readonly IHttpClientFactory _clientFactory;
        private readonly IMemoryCache _cache;
        private readonly ILogger<HackerNewsController> _logger;
        private const string BaseUrl = "https://hacker-news.firebaseio.com/v0";

        public HackerNewsController(IHttpClientFactory clientFactory, IMemoryCache cache, ILogger<HackerNewsController> logger)
        {
            _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }
        // Method to get the number of best stories by count
        [HttpGet("best/{count}")]
        public async Task<IActionResult> GetBestStories(int count)
        {
            try
            {
                _logger.LogInformation($"Received request for {count} best stories");

                if (count <= 0 || count > 500)
                {
                    return BadRequest("Count must be between 1 and 500");
                }

                var bestStoryIds = await GetBestStoryIdsAsync();
                _logger.LogInformation($"Retrieved {bestStoryIds?.Count ?? 0} story IDs");

                if (bestStoryIds == null || !bestStoryIds.Any())
                {
                    return StatusCode(503, "Unable to retrieve story IDs from Hacker News API");
                }

                var stories = new List<Story>();

                foreach (var id in bestStoryIds.Take(count))
                {
                    var story = await GetStoryAsync(id);
                    if (story != null)
                    {
                        stories.Add(story);
                    }
                }

                _logger.LogInformation($"Retrieved {stories.Count} stories");

                var sortedStories = stories.OrderByDescending(s => s.Score).ToList();

                if (!sortedStories.Any())
                {
                    return Ok(new { message = "No stories found", debug_info = new { story_ids = bestStoryIds.Take(count).ToList() } });
                }

                return Ok(sortedStories);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Error communicating with Hacker News API");
                return StatusCode(503, "Error communicating with Hacker News API");
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Error parsing JSON response from Hacker News API");
                return StatusCode(500, "Error parsing response from Hacker News API");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unexpected error occurred while processing the request");
                return StatusCode(500, $"An unexpected error occurred: {ex.Message}");
            }
        }

        private async Task<List<int>?> GetBestStoryIdsAsync()
        {
            return await _cache.GetOrCreateAsync("BestStoryIds", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);
                var client = _clientFactory.CreateClient();
                var response = await client.GetStringAsync($"{BaseUrl}/beststories.json");
                _logger.LogInformation($"Received best story IDs: {response}");
                return JsonSerializer.Deserialize<List<int>>(response);
            });
        }

        private async Task<Story?> GetStoryAsync(int id)
        {
            return await _cache.GetOrCreateAsync($"Story_{id}", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
                var client = _clientFactory.CreateClient();
                var response = await client.GetStringAsync($"{BaseUrl}/item/{id}.json");
                _logger.LogInformation($"Received story {id} details: {response}");
                var item = JsonSerializer.Deserialize<HackerNewsItem>(response);

                if (item?.Type != "story")
                {
                    _logger.LogWarning($"Item {id} is not a story. Type: {item?.Type}");
                    return null;
                }

                return new Story
                {
                    Title = item.Title ?? "No Title",
                    Uri = item.Url ?? "#",
                    PostedBy = item.By ?? "Unknown",
                    Time = DateTimeOffset.FromUnixTimeSeconds(item.Time).ToString("yyyy-MM-ddTHH:mm:sszzz"),
                    Score = item.Score,
                    CommentCount = item.Descendants
                };
            });
        }

        //Method to get all the best stories with the details ordered by 
        [HttpGet("bestStorie")]
        public async Task<IActionResult> BestStorie()
        {
            var client = _clientFactory.CreateClient();
            var bestStoriesResponse = await client.GetStringAsync($"{BaseUrl}/beststories.json");
            var storyIds = JsonSerializer.Deserialize<List<int>>(bestStoriesResponse);

            if (storyIds == null || storyIds.Count == 0)
            {
                return NotFound("No stories found");
            }

            var processedStories = new List<(JsonObject JsonObject, int score)>();

            foreach (var storyId in storyIds.Take(10)) // Process top 10 stories
            {
                var storyResponse = await client.GetStringAsync($"{BaseUrl}/item/{storyId}.json");
                var jsonObject = JsonNode.Parse(storyResponse)?.AsObject();

                if (jsonObject != null)
                {
                    jsonObject.Remove("kids");

                    if (jsonObject.TryGetPropertyValue("score", out var scoreNode) && scoreNode != null)
                    {
                        // Correctly extract the score as an integer
                        int score = scoreNode.GetValue<int>();
                        processedStories.Add((jsonObject, score)); // Store both the JSON object and its score
                    }

                   // processedStories.Add(jsonObject);
                }
            }

            // Sort stories by score in descending order
            var sortedStories = processedStories
                .OrderByDescending(story => story.score)
                .Select(story => story.JsonObject)
                .ToList();

            return Ok(new
            {
                best_stories = sortedStories
            });
        }
}
}
