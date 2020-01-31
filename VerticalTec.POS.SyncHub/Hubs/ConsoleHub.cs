﻿using Microsoft.AspNetCore.SignalR;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VerticalTec.POS.LiveUpdate;

namespace VerticalTec.POS.SyncHub.Hubs
{
    public class ConsoleHub : Hub<IConsoleHub>
    {
        IHubContext<LiveUpdateHub, ILiveUpdateClient> _hubContext;

        public ConsoleHub(IHubContext<LiveUpdateHub, ILiveUpdateClient> hubContext)
        {
            _hubContext = hubContext;
        }

        public async Task GetClientInfo(string connectionId)
        {
            await _hubContext.Clients.Client(connectionId).SyncVersion();
        }

        public async Task UpdateVersion(string connectionId)
        {
            await _hubContext.Clients.Client(connectionId).UpdateVersion();
        }

        public async Task Backup(string connectionId)
        {
            await _hubContext.Clients.Client(connectionId).Backup();
        }
    }
}
