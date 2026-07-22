using StreamJsonRpc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VerticalTec.POS.Database;

namespace VerticalTec.POS.Service.Ordering.Owin.Services
{
    public class OrderingRpcService
    {
        private readonly IDatabase _database;

        public OrderingRpcService(IDatabase database)
        {
            _database = database;
        }

        [JsonRpcMethod("add")]
        public async Task<OrderTransaction> AddOrderAsync(OrderTransaction orderData)
        {
            return orderData;
        }
    }
}
