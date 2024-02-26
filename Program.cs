using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static YouTubeChannelDetails;

class Program
{
    static readonly HttpClient notionClient = new HttpClient();
    static readonly HttpClient youtubeClient = new HttpClient();
    static string notionAccessToken;
    static string notionDatabaseId;
    static string youtubeApiKey;

    static async Task Main(string[] args)
    {
        var builder = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

        IConfigurationRoot configuration = builder.Build();

        notionAccessToken = configuration["Notion:AccessToken"];
        notionDatabaseId = configuration["Notion:DatabaseId"];
        youtubeApiKey = configuration["YouTube:ApiKey"];

        notionClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", notionAccessToken);
        notionClient.DefaultRequestHeaders.Add("Notion-Version", "2021-05-13");

        var namesAndIds = await GetNamesAndTagsFromNotion(notionDatabaseId);

        foreach (var item in namesAndIds)
        {
            string name = item.Key;
            string pageId = item.Value.PageId;


            if (!item.Value.Tags.Contains("YouTube"))
            {
                Console.WriteLine($"{name} does not have a YouTube tag. Skipping...");
                continue;
            }

            // Retrieve YouTube channel details (Channel ID, Subscriber Count, Thumbnail URL, Banner Image URL) in one API call to reduce unnecessary calls
            var youtubeDetails = await GetYouTubeChannelDetails(name);
            if (youtubeDetails != null)
            {
                // Update Notion with all YouTube details
                bool detailsUpdateResult = await UpdateNotionWithYouTubeDetails(pageId, youtubeDetails);
                if (detailsUpdateResult)
                {
                    Console.WriteLine($"Successfully updated {name} with YouTube details.");
                }
                else
                {
                    Console.WriteLine($"Failed to update {name}.");
                }

                // Update images if necessary
                await UpdateNotionPageWithCustomImage(pageId, youtubeDetails.ThumbnailUrl);
                await UpdateNotionPageWithCoverImage(pageId, youtubeDetails.BannerImageUrl);
            }
        }
    }

        static async Task<YouTubeChannelDetails> GetYouTubeChannelDetails(string name)
    {
        // Combine searches for channelId, subscriberCount, thumbnailUrl, and bannerImageUrl into one request where possible
        string searchUrl = $"https://www.googleapis.com/youtube/v3/search?part=snippet&q={name}&type=channel&key={youtubeApiKey}";
        var searchResponse = await youtubeClient.GetAsync(searchUrl);
        if (!searchResponse.IsSuccessStatusCode) return null;

        var searchContent = await searchResponse.Content.ReadAsStringAsync();
        var searchResult = JObject.Parse(searchContent);
        var channelId = searchResult["items"][0]["snippet"]["channelId"].ToString();
        var thumbnailUrl = searchResult["items"][0]["snippet"]["thumbnails"]["high"]["url"].ToString();

        string detailsUrl = $"https://www.googleapis.com/youtube/v3/channels?part=statistics,brandingSettings&id={channelId}&key={youtubeApiKey}";
        var detailsResponse = await youtubeClient.GetAsync(detailsUrl);
        if (!detailsResponse.IsSuccessStatusCode) return null;

        var detailsContent = await detailsResponse.Content.ReadAsStringAsync();
        var details = JObject.Parse(detailsContent);

        var subscriberCount = details["items"]?[0]["statistics"]["subscriberCount"]?.Value<long>() ?? 0;
        var viewCount = details["items"]?[0]["statistics"]["viewCount"]?.Value<long>() ?? 0;
        var videoCount = details["items"]?[0]["statistics"]["videoCount"]?.Value<long>() ?? 0;
        var bannerImageUrl = details["items"]?[0]["brandingSettings"]["image"]["bannerExternalUrl"]?.ToString() ?? "";
        var description = details["items"]?[0]["brandingSettings"]["channel"]["description"]?.ToString() ?? "";
        var keywords = details["items"]?[0]["brandingSettings"]["channel"]["keywords"]?.ToString() ?? "";


        return new YouTubeChannelDetails(channelId, subscriberCount, thumbnailUrl, bannerImageUrl, viewCount, videoCount, description, keywords);
    }

    static async Task<bool> PageIconExists(string pageId, string notionAccessToken)
    {
        var response = await notionClient.GetAsync($"https://api.notion.com/v1/pages/{pageId}");
        if (response.IsSuccessStatusCode)
        {
            var contentString = await response.Content.ReadAsStringAsync();
            var pageDetails = JObject.Parse(contentString);
            var iconProperty = pageDetails["icon"];
            return iconProperty != null; // Returns true if an icon exists, false otherwise
        }
        else
        {
            throw new Exception("Failed to retrieve page details.");
        }
    }


    static async Task<bool> UpdateNotionPageEmoji(string pageId, string emoji)
    {
        var updateJson = new JObject
        {
            ["icon"] = new JObject
            {
                ["type"] = "emoji",
                ["emoji"] = emoji
            }
        };

        var content = new StringContent(updateJson.ToString(), Encoding.UTF8, "application/json");

        var response = await notionClient.PatchAsync($"https://api.notion.com/v1/pages/{pageId}", content);

        return response.IsSuccessStatusCode;
    }

    // Hypothetical method to update a Notion page's cover image, assuming this feature exists
    static async Task<bool> UpdateNotionPageWithCoverImage(string pageId, string imageUrl)
    {

        var updateJson = new JObject
        {
            ["cover"] = new JObject
            {
                ["type"] = "external",
                ["external"] = new JObject
                {
                    ["url"] = imageUrl
                }
            }
        };

        var content = new StringContent(updateJson.ToString(), Encoding.UTF8, "application/json");
        var response = await notionClient.PatchAsync($"https://api.notion.com/v1/pages/{pageId}", content);

        return response.IsSuccessStatusCode;
    }
    static async Task<bool> UpdateNotionWithYouTubeDetails(string pageId, YouTubeChannelDetails details)
    {
        var updateData = new JObject
        {
            ["properties"] = new JObject
            {
                ["YouTube Subscribers"] = new JObject
                {
                    ["number"] = details.SubscriberCount
                },
                ["YouTube Views"] = new JObject
                {
                    ["number"] = details.ViewCount
                },
                ["YouTube Videos"] = new JObject
                {
                    ["number"] = details.VideoCount
                },
                ["YouTube Description"] = new JObject
                {
                    ["rich_text"] = new JArray
                {
                    new JObject
                    {
                        ["text"] = new JObject
                        {
                            ["content"] = details.Description
                        }
                    }
                }
                },
                ["YouTube Keywords"] = new JObject
                {
                    ["rich_text"] = new JArray
                {
                    new JObject
                    {
                        ["text"] = new JObject
                        {
                            ["content"] = details.Keywords
                        }
                    }
                }
                }
            }
        };

        // Note: You might need to adjust the property names ("YouTube Subscribers", "View Count", etc.) to match exactly with your Notion database properties.

        var content = new StringContent(updateData.ToString(), Encoding.UTF8, "application/json");
        var response = await notionClient.PatchAsync($"https://api.notion.com/v1/pages/{pageId}", content);

        return response.IsSuccessStatusCode;
    }

    static async Task<bool> UpdateNotionPageWithCustomImage(string pageId, string imageUrl)
    {
        var updateJson = new JObject
        {
            ["icon"] = new JObject
            {
                ["type"] = "external",
                ["external"] = new JObject
                {
                    ["url"] = imageUrl
                }
            }
        };

        var content = new StringContent(updateJson.ToString(), Encoding.UTF8, "application/json");

        var response = await notionClient.PatchAsync($"https://api.notion.com/v1/pages/{pageId}", content);

        return response.IsSuccessStatusCode;
    }

    static async Task<Dictionary<string, NotionPageInfo>> GetNamesAndTagsFromNotion(string databaseId)
    {
        var namesAndDetails = new Dictionary<string, NotionPageInfo>();
        var response = await notionClient.PostAsync($"https://api.notion.com/v1/databases/{databaseId}/query", null);
        var responseString = await response.Content.ReadAsStringAsync();
        var responseObject = JObject.Parse(responseString);

        foreach (var item in responseObject["results"])
        {
            string name = item["properties"]["Name"]["title"][0]["text"]["content"].ToString();
            string pageId = item["id"].ToString();
            var tags = new List<string>();

            var tagsArray = item["properties"]["Tags"]["multi_select"];
            foreach (var tag in tagsArray)
            {
                tags.Add(tag["name"].ToString());
            }

            namesAndDetails.Add(name, new NotionPageInfo(pageId, tags));
        }

        return namesAndDetails;
    }

    static async Task<bool> UpdateNotionWithYouTubeSubscribers(string pageId, long subscribers)
    {
        var updateData = new JObject
        {
            ["properties"] = new JObject
            {
                ["YouTube Subscribers"] = new JObject
                {
                    ["number"] = subscribers
                }
            }
        };

        var content = new StringContent(updateData.ToString(), System.Text.Encoding.UTF8, "application/json");
        var response = await notionClient.PatchAsync($"https://api.notion.com/v1/pages/{pageId}", content);

        return response.IsSuccessStatusCode;
    }
}

class YouTubeChannelDetails
{
    public string ChannelId { get; }
    public long SubscriberCount { get; }
    public long ViewCount { get; }
    public long VideoCount { get; }
    public string ThumbnailUrl { get; }
    public string BannerImageUrl { get; }
    public string Description { get; }
    public string Keywords { get; }

    public YouTubeChannelDetails(string channelId, long subscriberCount, string thumbnailUrl, string bannerImageUrl, long viewCount, long videoCount, string description, string keywords)
    {
        ChannelId = channelId;
        SubscriberCount = subscriberCount;
        ViewCount = viewCount;
        VideoCount = videoCount;
        ThumbnailUrl = thumbnailUrl;
        BannerImageUrl = bannerImageUrl;
        Description = description;
        Keywords = keywords;
    }
    public class NotionPageInfo
    {
        public string PageId { get; set; }
        public List<string> Tags { get; set; }

        public NotionPageInfo(string pageId, List<string> tags)
        {
            PageId = pageId;
            Tags = tags;
        }
    }
}
