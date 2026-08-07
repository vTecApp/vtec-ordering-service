using Hangfire;
using Hangfire.LiteDB;
using Microsoft.AspNet.SignalR;
using Owin;
using Swashbuckle.Application;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Web.Http;
using Unity;
using Unity.Injection;
using Unity.Lifetime;
using VerticalTec.POS.Database;
using VerticalTec.POS.Service.Ordering.Owin.Extensions;
using VerticalTec.POS.Service.Ordering.Owin.Models;
using VerticalTec.POS.Service.Ordering.Owin.Services;

namespace VerticalTec.POS.Service.Ordering.Owin
{
    public class Startup
    {
        IUnityContainer _container;

        public Startup(string dbServer, string dbName, string hangfileConnStr, string apiUser = "", string apiPass = "")
        {
            AppConfig.Instance.DbServer = dbServer;
            AppConfig.Instance.DbName = dbName;
            AppConfig.Instance.HangfileConnStr = hangfileConnStr;
            AppConfig.Instance.ApiUser = apiUser;
            AppConfig.Instance.ApiPass = apiPass;
        }

        public string Version
        {
            get
            {
                var assembly = Assembly.GetExecutingAssembly();
                var fvi = FileVersionInfo.GetVersionInfo(assembly.Location);
                var version = fvi.ProductVersion;
                return $"v{version}";
            }
        }

        private IEnumerable<IDisposable> GetHangfireServers()
        {
            try
            {
                var hangfire = System.IO.Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "hangfire.db");
                System.IO.File.Delete(hangfire);

                hangfire = System.IO.Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "hangfire-log.db");
                System.IO.File.Delete(hangfire);
            }
            catch { }

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

            _container = new UnityContainer();
            _container.RegisterType<IDatabase, MySqlDatabase>(new TransientLifetimeManager(),
                new InjectionConstructor(AppConfig.Instance.DbServer,
                AppConfig.Instance.DbName,
                AppConfig.Instance.DbPort));
            _container.RegisterType<IOrderingService, OrderingService>(new TransientLifetimeManager());
            _container.RegisterType<IPaymentService, PaymentService>(new TransientLifetimeManager());
            _container.RegisterSingleton<IMessengerService, MessengerService>();
            _container.RegisterSingleton<IPrintService, PrintService>();

            config.DependencyResolver = new UnityResolver(_container);

            var db = _container.Resolve<IDatabase>();
            DatabaseMigration.CheckAndUpdate(db, AppConfig.Instance.DbName);

            config.EnableSwagger(c =>
            {
                c.SingleApiVersion(Version, "Vtec Ordering Api");
            }).EnableSwaggerUi();
            config.Formatters.Remove(config.Formatters.XmlFormatter);
            config.Filters.Add(new GlobalExceptionHandler());
            config.MapHttpAttributeRoutes();

            appBuilder.MapSignalR("/signalkds", new HubConfiguration() { EnableDetailedErrors = true });
            appBuilder.UseHangfireAspNet(GetHangfireServers);
            appBuilder.UseHangfireDashboard("/jobs");
            appBuilder.UseWebApi(config);

            GlobalHost.Configuration.MaxIncomingWebSocketMessageSize = null;
        }
    }
}
