﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ErrorDashboard.Configuration
{
    public class RabbitMQConfiguration
    {
        public string ServerAddress { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
    }
}
