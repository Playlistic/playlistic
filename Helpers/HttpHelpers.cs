﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Youtube2Spotify.Helpers
{
    public static class HttpHelpers
    {
        public static HttpWebResponse MakePostRequest(string url, string postData, string token)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            var data = Encoding.UTF8.GetBytes(postData);
            request.ContentLength = data.Length;
            request.Method = "POST";
            request.ContentType = "application/json;charset=utf-8";
            request.Accept = "application/json";
            request.Headers.Add("Authorization", "Bearer " + token);
            request.Timeout = 300000;

            using (var stream = request.GetRequestStream())
            {
                stream.Write(data, 0, data.Length);
            }

            return (HttpWebResponse)request.GetResponse();
        }
    }
}