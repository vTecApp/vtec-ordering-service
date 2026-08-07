using Org.BouncyCastle.Asn1.Ocsp;
using StreamJsonRpc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VerticalTec.POS.Database;
using VerticalTec.POS.Service.Ordering.Owin.Models;

namespace VerticalTec.POS.Service.Ordering.Owin.Services
{
    public class OrderingRpcService
    {
        private readonly IDatabase _database;
        private readonly IOrderingService _orderService;

        public OrderingRpcService(IDatabase database, IOrderingService orderService)
        {
            _database = database;
            _orderService = orderService;
        }

        [JsonRpcMethod("add")]
        public async Task<OrderTransaction> AddOrderAsync(OrderTransaction orderData)
        {
            using (var conn = await _database.ConnectAsync())
            {
                try
                {
                    await _orderService.AddOrderAsync(conn, orderData);
                }
                catch
                {

                }
            }
            return orderData;
        }
    }
}
