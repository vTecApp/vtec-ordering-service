using Microsoft.Owin;
using StreamJsonRpc;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity;

namespace VerticalTec.POS.Service.Ordering.Owin.Middlewares
{
    using AppFunc = Func<IDictionary<string, object>, Task>;

    public class JsonRpcMiddleware
    {
        private readonly AppFunc _next;
        private readonly IUnityContainer _container;
        private readonly Type _serviceType;

        public JsonRpcMiddleware(AppFunc next, IUnityContainer container, Type serviceType)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _container = container ?? throw new ArgumentNullException(nameof(container));
            _serviceType = serviceType ?? throw new ArgumentNullException(nameof(serviceType));
        }

        public async Task Invoke(IDictionary<string, object> environment)
        {
            var context = new OwinContext(environment);

            if (context.Request.Method == "POST" && context.Request.Path.Value == "/rpc")
            {
                using (var childContainer = _container.CreateChildContainer())
                {
                    var serviceInstance = childContainer.Resolve(_serviceType);

                    using (var handler = new HeaderDelimitedMessageHandler(context.Response.Body, context.Request.Body))
                    using (var jsonRpc = new JsonRpc(handler, serviceInstance))
                    {
                        jsonRpc.StartListening();
                        await jsonRpc.Completion;
                    }
                }
                return;
            }

            await _next(environment);
        }
    }
}
