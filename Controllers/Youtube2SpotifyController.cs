﻿using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SpotifyAPI.Web;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using Playlistic.Helpers;
using Playlistic.Models;
using Microsoft.AspNetCore.Hosting;

namespace Playlistic.Controllers
{
    public class YoutubePlaylistMetadata
    {
        public string title;
        public string description;
        public string coverImageInBase64;
    }
    public class VerificationObject
    {
        public int Index;
        public string OriginalYoutubeName;
        public string FoundSpotifyName;

        public VerificationObject(int index, string originalYoutubeName, string foundSpotifyName)
        {
            Index = index;
            OriginalYoutubeName = originalYoutubeName;
            FoundSpotifyName = foundSpotifyName;
        }
    }

    public class Youtube2SpotifyController : Controller
    {
        private readonly IConfiguration _configuration;
        private readonly IWebHostEnvironment _hostingEnvironment;
        public Youtube2SpotifyController(IConfiguration configuration, IWebHostEnvironment hostingEnvironment)
        {
            _hostingEnvironment = hostingEnvironment;
            _configuration = configuration;
            openAIAccessToken = configuration["OpenAIKey"];
            openAIAssistantSetupString = configuration["OpenAIPrompt"];
        }

        private string YoutubePlaylistID { get; set; }
        private YoutubePlaylistMetadata youtubePlaylistMetadata;
        private SpotifyClient spotify;
        private dynamic InitialData;
        private readonly string openAIAccessToken;
        private readonly string openAIAssistantSetupString;


        public async Task<IActionResult> Index(string youtubePlaylistID)
        {
            ResultModel resultModel = new();
            HomeModel homeModel = new(false);
            string access_Token = HttpContext.Session.GetString("access_token");
            try
            {
                string expire_time_raw = HttpContext.Session.GetString("expire_time");
                DateTime expire_Time = DateTime.Parse(expire_time_raw);
                if (DateTime.Now > expire_Time)
                {
                    return Home(homeModel);
                }
            }
            catch
            {
                return Home(homeModel);
            }

            homeModel.SetAuthenticated(true);

            spotify = new SpotifyClient(access_Token);
            HttpContext.Session.SetString("user_Id", await GetUserId());

            if (!string.IsNullOrEmpty(youtubePlaylistID) || !string.IsNullOrWhiteSpace(youtubePlaylistID))
            {
                YoutubePlaylistID = youtubePlaylistID;
                resultModel = await ConvertYoutubePlaylist2SpotifyPlaylist(youtubePlaylistID);
            }
            else
            {
                return Home(homeModel);
            }

            return Result(resultModel);
        }

        public PartialViewResult Result(ResultModel result = null)
        {
            return PartialView("~/Views/Result/Index.cshtml", result);
        }

        public PartialViewResult Home(HomeModel home = null)
        {
            return PartialView("~/Views/Home/Index.cshtml", home);
        }

        private static dynamic GetYoutubePlaylistDataFromHTML(string playlistId)
        {
            // youtube... in their infinite wisdom...
            // decided to not include certain songs within their playlist (basically videos provided by youtube music with "- topic"
            // in the channel name), rendering their playlistitem api unreliable

            // grab the playlist from music.youtube.com 

            string html;
            HttpWebRequest request = WebRequest.Create($"https://music.youtube.com/playlist?list={playlistId}") as HttpWebRequest;
            request.AutomaticDecompression = DecompressionMethods.GZip;
            request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/102.0.5005.63 Safari/537.36 Edg/102.0.1245.33";
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (StreamReader reader = new(stream))
            {
                html = reader.ReadToEnd();
            }

            HtmlParser parser = new();
            IHtmlDocument document = parser.ParseDocument(html);
            List<IElement> listOfScript = document.Body.GetElementsByTagName("script").ToList();
            IElement element = listOfScript.First(x => x.TextContent.Contains("initialData.push"));
            string ytInitialData = element.InnerHtml;

            // people put all kinds of characters in metadata, gross
            ytInitialData = Regex.Unescape(ytInitialData);
            ytInitialData = HttpUtility.HtmlDecode(ytInitialData);
            string rawPlaylistData = ytInitialData.Split("data: '").Last();
            rawPlaylistData = rawPlaylistData.Replace("'});ytcfg.set({'YTMUSIC_INITIAL_DATA': initialData});} catch (e) {}", "");

            dynamic initialData = JsonConvert.DeserializeObject(rawPlaylistData);           
            return initialData;
        }

        private static JArray GetPlaylistItem(dynamic initialData)
        {
            JArray playlist = initialData.contents.singleColumnBrowseResultsRenderer.tabs[0].tabRenderer.content.sectionListRenderer.contents[0].musicPlaylistShelfRenderer.contents;
            return playlist;
        }

        /// <summary>
        /// get title and artist name from youtube
        /// </summary>
        /// <param name="youtubePlaylistId"></param>
        /// <returns></returns>
        public async Task<ResultModel> ConvertYoutubePlaylist2SpotifyPlaylist(string youtubePlaylistId)
        {
            ResultModel resultModel = new();
            List<Playlistic_PlaylistItem> PlaylistItems = new();           

            //youtube data api have now comletely failed for any playlist other than user created playlist
            //this is not acceptable, now getting metadata directly through youtube music
            try
            {
                //collect playlist data(including metadata and playlist items) from youtube music 
                InitialData = GetYoutubePlaylistDataFromHTML(youtubePlaylistId);
            }
            catch (Exception ex)
            {
                //something is weird with the youtubePlaylistId
                return new ResultModel(FaultCode.Unspported, $"https://youtube.com/playlist?list={youtubePlaylistId}");
            }

            try
            {
                youtubePlaylistMetadata = GenerateYoutubePlaylistMetadata(InitialData);

                List<string> songNames = new();

                JArray playlist = GetPlaylistItem(InitialData);

                if (playlist == null)
                {
                    //empty playlist, halt further processing
                    return new ResultModel(FaultCode.EmptyPlaylist, $"https://youtube.com/playlist?list={youtubePlaylistId}");
                }

                PlaylistItems = GetPreliminaryPlaylistItems(playlist);

                int numIterations = PlaylistItems.Count / 10;
                List<Playlistic_PlaylistItem> Results = new();
                // break input list into sublist of max 10 items
                for (int i = 0; i < numIterations; i++)
                {
                    var Sublist = PlaylistItems.Take(new Range(i * 10, i * 10 + 10));
                    var SubPlaylistItems = PlaylistItemFactory.CleanUpPlaylistItems_PoweredByAI(Sublist.ToList(), openAIAssistantSetupString, openAIAccessToken);
                    Results.AddRange(SubPlaylistItems);
                }

                PlaylistItems = Results;
                PlaylistItems = await SearchForSongsOnSpotify(PlaylistItems);

                if (PlaylistItems.Any(x => { return x.FoundSpotifyTrack != null; }))
                {
                    // break input list into sublist of max 10 items
                    // process each sublist with chatgpt
                    // merge the output of the sublist and output the list

                    // add total number of song names
                    // okay, we got the title, time to look it up on Spotify
                    string newSpotifyPlaylistID = await CreateEmptyPlayListOnSpotify(youtubePlaylistMetadata);

                    await UploadCoverToPlaylist(newSpotifyPlaylistID, youtubePlaylistMetadata);


                    List<VerificationObject> verificationObjects = PlaylistItems.Select(x => { return new VerificationObject(PlaylistItems.IndexOf(x), x.SpotifySearchObject.Song, x.FoundSpotifyTrack.Name); }).ToList();
                    string jsonString = JsonConvert.SerializeObject(verificationObjects);

                    bool success = AddTrackToSpotifyPlaylist(newSpotifyPlaylistID, PlaylistItems.Select(x => { return x.FoundSpotifyTrack; }).ToList());

                    if (success)
                    {
                        resultModel.PlaylistItems = PlaylistItems;
                        resultModel.SpotifyLink = $"https://open.spotify.com/playlist/{newSpotifyPlaylistID}";
                        resultModel.YoutubeLink = $"https://youtube.com/playlist?list={youtubePlaylistId}";
                        string resultModelString = JsonConvert.SerializeObject(resultModel);
                        return resultModel;
                    }
                    throw new Exception("Failed to add tracks to spotify");
                }
                else
                {
                    return new ResultModel(FaultCode.EmptySearchResult, $"https://youtube.com/playlist?list={youtubePlaylistId}");
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine(exception.Message);
                Console.WriteLine(exception.StackTrace);
                return new ResultModel(FaultCode.ConversionFailed, $"https://youtube.com/playlist?list={youtubePlaylistId}");
            }

        }

        /// <summary>
        /// Simulate a conversion result without having to execute the actual conversion process
        /// </summary>
        /// <returns></returns>
        public PartialViewResult FrontEndTest()
        {
            return PartialView("~/Views/Result/Index.cshtml", JsonConvert.DeserializeObject<ResultModel>(System.IO.File.ReadAllText($"{_hostingEnvironment.WebRootPath}\\SampleData.json")));
        }

        public YoutubePlaylistMetadata GenerateYoutubePlaylistMetadata(dynamic playlistData)
        {
            string title = playlistData.header.musicDetailHeaderRenderer.title.runs[0].text;
            string description = playlistData.header.musicDetailHeaderRenderer.description.runs[0].text;
            string coverArt = playlistData.header.musicDetailHeaderRenderer.thumbnail.croppedSquareThumbnailRenderer.thumbnail.thumbnails[1].url;

            string base64ImageString = string.Empty;

            //spotify have a title and description limit of 200 characters, capping that
            if (title.Length > 200)
            {
                title = title[..200];
            }

            if (description.Length > 200)
            {
                description = description[..200];
            }

            try
            {
                using Stream stream = GetStreamFromUrl(coverArt);
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                byte[] fileBytes = ms.ToArray();
                base64ImageString = Convert.ToBase64String(fileBytes);
            }
            catch
            {
                base64ImageString = string.Empty;
            }

            return new YoutubePlaylistMetadata()
            {
                title = title,
                description = description,
                coverImageInBase64 = base64ImageString
            };
        }

        private static List<Playlistic_PlaylistItem> GetPreliminaryPlaylistItems(JArray incomingRawYoutubeMusicPlaylistData)
        {
            List<Playlistic_PlaylistItem> OriginalYoutubeData = new();
            foreach (dynamic musicResponsiveListItemRenderer in incomingRawYoutubeMusicPlaylistData)
            {
                string songName = musicResponsiveListItemRenderer.musicResponsiveListItemRenderer.flexColumns[0].musicResponsiveListItemFlexColumnRenderer.text.runs[0].text.Value.ToString();
                string songArtists = musicResponsiveListItemRenderer.musicResponsiveListItemRenderer.flexColumns[1].musicResponsiveListItemFlexColumnRenderer.text.runs[0].text.Value.ToString();
                string originalYoutubeThumbnailSmall = musicResponsiveListItemRenderer.musicResponsiveListItemRenderer.thumbnail.musicThumbnailRenderer.thumbnail.thumbnails[0].url.Value;

                string originalYoutubeVideoId = string.Empty;

                try
                {
                    originalYoutubeVideoId = musicResponsiveListItemRenderer.musicResponsiveListItemRenderer.playlistItemData.videoId.Value;
                }
                catch
                {

                    string pattern = @"/([a-zA-Z0-9_-]+)\.[a-z]+$";

                    // The match object
                    Match match = Regex.Match(musicResponsiveListItemRenderer.musicResponsiveListItemRenderer.thumbnail.musicThumbnailRenderer.thumbnail.thumbnails[0].url.Value, pattern);

                    // If a match is found, extract the video id
                    if (match.Success)
                    {
                        originalYoutubeVideoId = match.Groups[1].Value;
                    }


                }

                Playlistic_PlaylistItem playlistItem = new();
                songName = songName.Replace("\"", "");
                playlistItem.SpotifySearchObject.Song = songName;
                playlistItem.OriginalYoutubeObject.VideoId = originalYoutubeVideoId;
                playlistItem.OriginalYoutubeObject.ThumbnailURL = originalYoutubeThumbnailSmall;
                playlistItem.OriginalYoutubeObject.VideoChannelTitle = songArtists;
                playlistItem.OriginalYoutubeObject.VideoTitle = songName;
                playlistItem.SpotifySearchObject.Artists.Add(songArtists.ToLower());
                OriginalYoutubeData.Add(playlistItem);
            }
            return OriginalYoutubeData;
        }

        /// <summary>
        /// Creates an Empty Playlist
        /// </summary>
        /// <param name="youtubePlaylistItems"></param>
        public async Task<string> CreateEmptyPlayListOnSpotify(YoutubePlaylistMetadata youtubePlaylistMetadata)
        {
            //create a playlist using the currently authenticated profile
            string user_Id = HttpContext.Session.GetString("user_Id");

            PlaylistCreateRequest playlistCreateRequest = new(youtubePlaylistMetadata.title)
            {
                Description = youtubePlaylistMetadata.description
            };

            FullPlaylist fullPlaylist = await spotify.Playlists.Create(user_Id, playlistCreateRequest);
            return fullPlaylist.Id;
        }
        /// <summary>
        /// Uploads playlist cover to specified 
        /// </summary>
        /// <param name="SpotifyPlaylistId"></param>
        /// <returns></returns>
        public async Task<bool> UploadCoverToPlaylist(string SpotifyPlaylistId, YoutubePlaylistMetadata youtubePlaylistMetadata)
        {
            bool uploadCover = false;
            if (!string.IsNullOrEmpty(youtubePlaylistMetadata.coverImageInBase64))
            {
                uploadCover = await spotify.Playlists.UploadCover(SpotifyPlaylistId, youtubePlaylistMetadata.coverImageInBase64);
            }

            return uploadCover;
        }

        /// <summary>
        /// returns a list of corresponding track on spotify based on the list of youtube playlist items
        /// </summary>
        /// <param name="playlistItems">incoming list of youtube videos</param>
        /// <returns></returns>
        public async Task<List<Playlistic_PlaylistItem>> SearchForSongsOnSpotify(List<Playlistic_PlaylistItem> playlistItems)
        {
            foreach (Playlistic_PlaylistItem playlistItem in playlistItems)
            {
                SearchRequest searchRequest = new(SearchRequest.Types.Track, FormatSpotifySearchString(playlistItem))
                {
                    Limit = 1
                };

                SearchResponse searchResponse = await spotify.Search.Item(searchRequest);

                if (searchResponse.Tracks.Items != null)
                {
                    if (searchResponse.Tracks.Items.Count > 0)
                    {
                        FullTrack fullTrack = searchResponse.Tracks.Items[0];
                        playlistItem.FoundSpotifyTrack = fullTrack;
                    }
                }
            }
            return playlistItems;
        }
        public bool AddTrackToSpotifyPlaylist(string spotifyPlaylistId, List<FullTrack> tracksToAdd)
        {
            List<string> trackURI = new();

            foreach (FullTrack fullTrack in tracksToAdd)
            {
                if (fullTrack != null)
                {
                    List<SimpleArtist> artists = fullTrack.Artists;
                    List<string> artistNames = artists.Select(x => x.Name).ToList();
                    string songName = fullTrack.Name.ToString();
                    trackURI.Add($"\"{fullTrack.Uri}\"");
                }
                continue;
            }

            try
            {
                HttpWebResponse httpWebResponse = AddTracksToPlaylist(spotifyPlaylistId, string.Join(",", trackURI));
                if (httpWebResponse.StatusCode == HttpStatusCode.Created)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                return false;
            }

            return false;
        }

        private static Stream GetStreamFromUrl(string url)
        {
            byte[] imageData = null;

            using (var wc = new WebClient())
                imageData = wc.DownloadData(url);

            return new MemoryStream(imageData);
        }

        private string FormatResultString(string song, List<string> artists)
        {
            return $"{string.Join(", ", artists)} - {song}";
        }

        private static string FormatSpotifySearchString(Playlistic_PlaylistItem playlistItem)
        {
            StringBuilder queryBuilder = new();

            queryBuilder.Append(string.Join(" ", playlistItem.SpotifySearchObject.Artists));
            queryBuilder.Append($" {playlistItem.SpotifySearchObject.Song}");

            return queryBuilder.ToString();
        }

        public HttpWebResponse AddTracksToPlaylist(string newSpotifyPlaylistID, string tracksToAdd)
        {
            string url = $"https://api.spotify.com/v1/playlists/{newSpotifyPlaylistID}/tracks";

            string postData = "{";
            postData += "\"uris\": " + $"[{tracksToAdd}]";
            postData += "}";

            return HttpHelpers.MakePostRequest(url, postData, HttpContext.Session.GetString("access_token"));
        }

        public async Task<string> GetUserId()
        {
            string user_Id = HttpContext.Session.GetString("user_id");
            // if we already stored the UserId in session, grab it and return it 
            if (!string.IsNullOrEmpty(user_Id))
            {
                return user_Id;
            }

            return await GetUserIdLive();
        }

        private async Task<string> GetUserIdLive()
        {
            PrivateUser privateUser = await spotify.UserProfile.Current();
            return privateUser.Id;
        }
    }
}
