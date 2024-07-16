﻿using Hangfire;
using Hangfire.LiteDB;
using Microsoft.AspNet.SignalR;
using Owin;
using Swashbuckle.Application;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Cors;
using Unity;
using Unity.Injection;
using Unity.Lifetime;
using VerticalTec.POS.Database;
using VerticalTec.POS.Service.Ordering.Owin.Models;
using VerticalTec.POS.Service.Ordering.Owin.Services;
using VerticalTec.POS.Utils;

namespace VerticalTec.POS.Service.Ordering.Owin
{
    public class Startup
    {
        IUnityContainer _container;

        public Startup(string dbServer, string dbName, string hangfileConnStr)
        {
            AppConfig.Instance.DbServer = dbServer;
            AppConfig.Instance.DbName = dbName;
            AppConfig.Instance.HangfileConnStr = hangfileConnStr;
        }

        public Startup(string dbServer, string dbName, string hangfileConnStr, string rcAgentPath)
        {
            AppConfig.Instance.DbServer = dbServer;
            AppConfig.Instance.DbName = dbName;
            AppConfig.Instance.HangfileConnStr = hangfileConnStr;
            AppConfig.Instance.RCAgentPath = rcAgentPath;
        }

        public string Version
        {
            get
            {
                var assembly = Assembly.GetExecutingAssembly();
                var fvi = FileVersionInfo.GetVersionInfo(assembly.Location);
                var version = fvi.ProductVersion;
                return $"v{version}(AOT)";
            }
        }

        private IEnumerable<IDisposable> GetHangfireServers()
        {
            GlobalConfiguration.Configuration
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_170)
                .UseUnityActivator(_container)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UseLiteDbStorage(AppConfig.Instance.HangfileConnStr);

            yield return new BackgroundJobServer();
        }

        public void Configuration(IAppBuilder appBuilder)
        {
            HttpConfiguration config = new HttpConfiguration();
            var cors = new EnableCorsAttribute("*", "*", "*");
            config.EnableCors(cors);

            _container = new UnityContainer();
            _container.RegisterType<IDatabase, MySqlDatabase>(new TransientLifetimeManager(),
                new InjectionConstructor(AppConfig.Instance.DbServer,
                AppConfig.Instance.DbName,
                AppConfig.Instance.DbPort));
            _container.RegisterType<IOrderingService, OrderingService>(new TransientLifetimeManager());
            _container.RegisterType<IPaymentService, PaymentService>(new TransientLifetimeManager());
            _container.RegisterSingleton<IMessengerService, MessengerService>();
            _container.RegisterSingleton<IPrintService, PrintService>();
            _container.RegisterSingleton<AOTRCAgentService>();

            config.DependencyResolver = new UnityResolver(_container);

            var db = _container.Resolve<IDatabase>();
            DatabaseMigration.CheckAndUpdate(db, AppConfig.Instance.DbName);

            GlobalHost.DependencyResolver.Register(typeof(KDSHub), () => 
                new KDSHub(db, _container.Resolve<VtecPOSRepo>(), 
                    _container.Resolve<IPrintService>(), 
                    _container.Resolve<IMessengerService>()));
            GlobalHost.Configuration.MaxIncomingWebSocketMessageSize = null;

            config.EnableSwagger(c => c.SingleApiVersion(Version, "Vtec Ordering Api")).EnableSwaggerUi();
            config.Formatters.Remove(config.Formatters.XmlFormatter);
            config.Filters.Add(new GlobalExceptionHandler());
            config.MapHttpAttributeRoutes();

            appBuilder.MapSignalR("/signalkds", new HubConfiguration() { EnableDetailedErrors = true });
            appBuilder.UseHangfireAspNet(GetHangfireServers);
            appBuilder.UseHangfireDashboard("/jobs");
            appBuilder.UseWebApi(config);
        }
    }
}
