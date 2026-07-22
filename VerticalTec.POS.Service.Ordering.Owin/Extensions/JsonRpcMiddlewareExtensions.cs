using Owin;
using Unity;
using VerticalTec.POS.Service.Ordering.Owin.Middlewares;

namespace VerticalTec.POS.Service.Ordering.Owin.Extensions
{
    public static class JsonRpcMiddlewareExtensions
    {
        public static IAppBuilder UseJsonRpc<TService>(this IAppBuilder app, IUnityContainer container)
        {
            return app.Use<JsonRpcMiddleware>(container, typeof(TService));
        }
    }
}
