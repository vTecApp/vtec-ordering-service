using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VerticalTec.POS.Database;

namespace VerticalTec.POS.LiveUpdate.Console
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var host = CreateHostBuilder(args).Build();
            try
            {
                var db = host.Services.GetRequiredService<IDatabase>();
                var dbCtx = host.Services.GetRequiredService<LiveUpdateDbContext>();
                Task.Run(async () =>
                {
                    using (var conn = await db.ConnectAsync())
                    {
                        await dbCtx.UpdateStructure(conn);
                    }
                });
            }
            catch { }
            host.Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
            .UseWindowsService()
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                });
    }
}
