using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VerticalTec.POS.Database;
using VerticalTec.POS.LiveUpdate;

namespace VerticalTec.POS.Service.LiveUpdate
{
    public class Program
    {
        public static void Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
            .UseWindowsService()
            .ConfigureServices((context, services) =>
            {
                var connStr = context.Configuration.GetConnectionString("VtecPOS");
                services.AddSingleton<IDatabase>(db => new MySqlDatabase(connStr));
                services.AddSingleton<LiveUpdateDbContext>();
                services.AddSingleton<FrontConfigManager>();
                services.AddHostedService<LiveUpdateService>();
            });
    }
}
