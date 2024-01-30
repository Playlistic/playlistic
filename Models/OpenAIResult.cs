﻿using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Playlistic.Models
{
    public class OpenAIResult
    {
        public string id { get; set; }
        public string _object { get; set; }
        public int created { get; set; }
        public string model { get; set; }
        public Usage usage { get; set; }
        public Choice[] choices { get; set; }
    }
    public class Usage
    {
        public int prompt_tokens { get; set; }
        public int completion_tokens { get; set; }
        public int total_tokens { get; set; }
    }
    public class Choice
    {
        public Message message { get; set; }
        public string finish_reason { get; set; }
        public int index { get; set; }
    }
    public class Message
    {
        public string role { get; set; }
        public string content { get; set; }
    }
    public class JsonAIResult
    {

        public List<SpotifySearchObject> spotifySearchObjects;
        [JsonIgnore]
        public object Comments;
        [JsonIgnore]
        public object Messages;
    }
}
