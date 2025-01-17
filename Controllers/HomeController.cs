﻿using System;
using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Playlistic.Models;

namespace Playlistic.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;
        private HomeModel homeModel;
        public HomeController(ILogger<HomeController> logger)
        {
            _logger = logger;
        }

        public IActionResult Index()
        {
            homeModel = new HomeModel(false);
            HttpContext.Session.TryGetValue("access_token", out byte[] access_token);
            HttpContext.Session.TryGetValue("expire_time", out byte[] expire_time_raw);


            if (access_token != null && expire_time_raw != null)
            {
                DateTime expire_time = DateTime.Parse(new string(System.Text.Encoding.Default.GetString(expire_time_raw)));

                if (DateTime.Now < expire_time)
                {
                    homeModel.SetAuthenticated(true);
                }
            }

            return View(homeModel);
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}
