﻿using MySql.Data.MySqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using VerticalTec.POS.Database;
using vtecPOS.GlobalFunctions;

namespace VerticalTec.POS.Core
{
    public class POSWrapperMySql : IPOS
    {
        const string TableSubffix = "front";

        IDatabase _database;
        POSModule _posModule;

        public POSWrapperMySql(IDatabase database)
        {
            _database = database;
            _posModule = new POSModule();
        }

        public Task AddOrderAsync(IDbConnection conn, IEnumerable<Order> orders)
        {
            var responseText = "";
            foreach (var order in orders)
            {
                if (order.ParentProductId > 0 && order.OrderDetailLinkId == 0)
                {
                    order.OrderDetailLinkId = orders
                        .Where(o => order.ParentProductId == o.ProductId)
                        .Select(o => o.OrderDetailId).FirstOrDefault();
                }

                var orderDetailId = 0;
                var isSuccess = _posModule.OrderDetail(ref responseText, ref orderDetailId, false,
                    (int)order.SaleMode,
                    order.TransactionId, order.ComputerId, order.OrderDetailLinkId,
                    order.IndentLevel, order.ProductId, order.TotalQty,
                    order.OpenPrice, TableSubffix, GlobalVar.Instance.DecimalDigit,
                    GlobalVar.Instance.SaleDate, order.ShopId,
                    order.IsComponentProduct, order.ParentProductId,
                    order.PGroupId, order.OtherFoodName, order.OtherProductGroupId,
                    order.OtherPrinterId, order.OtherDiscountAllow,
                    order.OtherInventoryId, order.OtherVatType, order.OtherPrintGroup,
                    order.OtherProductVatCode, order.OtherHasSc, order.OtherProductTypeId,
                    order.StaffId, order.ComputerId, order.TableId,
                    order.SetGroupNo, order.QtyRatio, conn as MySqlConnection);
                if (isSuccess)
                {
                    order.OrderDetailId = orderDetailId;
                }
                else
                {
                    throw new POSModuleException("OrderDetail", responseText);
                }
            }
            return Task.FromResult(true);
        }

        public async Task AddPaymentAsync(IDbConnection conn, Payment payment)
        {
            var tranIdParam = _database.CreateParameter("@transactionId", payment.TransactionId);
            var compIdParam = _database.CreateParameter("@computerId", payment.ComputerId);
            var shopIdParam = _database.CreateParameter("@shopId", payment.ShopId);
            var payTypeIdParam = _database.CreateParameter("@payTypeId", payment.PayTypeId);
            var currencyCode = "USD";
            var currencyName = "US Dollar";
            var currencyRatio = 1.0M;
            var exchangeRate = 1.0M;
            var changeExchangeRate = 1.0M;

            try
            {
                IDbCommand cmd = _database.CreateCommand(conn);
                cmd.CommandText = "select a.CurrencyCode, a.CurrencyName, " +
                    "b.CurrencyRatio, b.ExchangeRate, b.ChangeExchangeRate " +
                    "from payment_currency a " +
                    "left join payment_exratetable b " +
                    "on a.CurrencyID=b.CurrencyID where a.IsMainCurrency=1";
                using (IDataReader reader = await _database.ExecuteReaderAsync(cmd))
                {
                    if (reader.Read())
                    {
                        currencyCode = reader.GetValue<string>("CurrencyCode");
                        currencyName = reader.GetValue<string>("CurrencyName");
                        currencyRatio = reader.GetValue<decimal>("CurrencyRatio");
                        exchangeRate = reader.GetValue<decimal>("ExchangeRate");
                        changeExchangeRate = reader.GetValue<decimal>("ChangeExchangeRate");
                    }
                }

                cmd.CommandText = "select PayDetailID " +
                    " from orderpaydetailfront " +
                    " where TransactionID=@transactionId " +
                    " and ComputerID=@computerId " +
                    " and PayTypeID=@payTypeId";
                cmd.Parameters.Add(tranIdParam);
                cmd.Parameters.Add(compIdParam);
                cmd.Parameters.Add(payTypeIdParam);
                using (IDataReader reader = await _database.ExecuteReaderAsync(cmd))
                {
                    if (reader.Read())
                    {
                        payment.PaymentId = reader.GetValue<int>("PayDetailID");
                    }
                }

                decimal receiptPayPrice = 0;
                cmd.CommandText = "select ReceiptPayPrice from ordertransactionfront " +
                    "where TransactionID=@transactionId and ComputerID=@computerId";
                cmd.Parameters.Clear();
                cmd.Parameters.Add(tranIdParam);
                cmd.Parameters.Add(compIdParam);
                using (IDataReader reader = await _database.ExecuteReaderAsync(cmd))
                {
                    if (reader.Read())
                        receiptPayPrice = reader.GetValue<decimal>("ReceiptPayPrice");
                }

                bool isUpdate = payment.PaymentId > 0;
                if (isUpdate)
                {
                    cmd.CommandText = "update orderpaydetailfront " +
                        " set PayAmount = PayAmount + @payAmount, " +
                        " CurrencyAmount = PayAmount * @exchangeRate, " +
                        " CashChange = (PayAmount + @payAmount) - @receiptPayPrice, " +
                        " CashChangeMainCurrency = ((PayAmount + @payAmount) - @receiptPayPrice) * @exchangeRate " +
                        " where TransactionID=@transactionId " +
                        " and ComputerID=@computerId " +
                        " and PayTypeID=@payTypeId";
                    cmd.Parameters.Clear();
                    cmd.Parameters.Add(_database.CreateParameter("@payAmount", payment.PayAmount));
                    cmd.Parameters.Add(_database.CreateParameter("@receiptPayPrice", receiptPayPrice));
                    cmd.Parameters.Add(_database.CreateParameter("@exchangeRate", exchangeRate));
                    cmd.Parameters.Add(tranIdParam);
                    cmd.Parameters.Add(compIdParam);
                    cmd.Parameters.Add(payTypeIdParam);
                }
                else
                {
                    cmd.CommandText = "select case when max(PayDetailID) is null then 1 " +
                        "else max(PayDetailID) + 1 end as PayDetailID " +
                        "from orderpaydetailfront " +
                        "where TransactionID=@transactionId and ComputerID=@computerId";
                    cmd.Parameters.Clear();
                    cmd.Parameters.Add(tranIdParam);
                    cmd.Parameters.Add(compIdParam);
                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            payment.PaymentId = reader.GetValue<int>("PayDetailID");
                        }
                    }

                    cmd.CommandText = "insert into orderpaydetailfront " +
                        "(PayDetailID, TransactionID, ComputerID, TranKey, PayTypeID, " +
                        "PayAmount, CurrencyCode, CurrencyName, CurrencyRatio, ExchangeRate, CurrencyAmount, " +
                        "CashChange, CashChangeMainCurrency, CashChangeMainCurrencyCode, " +
                        "CreditCardType, ShopID, SaleDate) " +
                        "values(@payDetailId, @transactionId, @computerId, @tranKey, @payTypeId, @payAmount, " +
                        "@currencyCode, @currencyName, @currencyRatio, @exchangeRate, @currencyAmount, " +
                        "@cashChange, @cashChangeMainCurrency, @cashChangeMainCurrencyCode, " +
                        "@creditCardType, @bankNameId, @shopId, @saleDate)";
                    cmd.Parameters.Clear();
                    cmd.Parameters.Add(tranIdParam);
                    cmd.Parameters.Add(compIdParam);
                    cmd.Parameters.Add(payTypeIdParam);
                    cmd.Parameters.Add(shopIdParam);
                    cmd.Parameters.Add(_database.CreateParameter("@paydetailId", payment.PaymentId));
                    cmd.Parameters.Add(_database.CreateParameter("@tranKey", $"{payment.TransactionId}:{payment.ComputerId}"));
                    cmd.Parameters.Add(_database.CreateParameter("@payAmount", payment.PayAmount));
                    cmd.Parameters.Add(_database.CreateParameter("@creditCardType", payment.CreditCardType));
                    cmd.Parameters.Add(_database.CreateParameter("@currencyCode", currencyCode));
                    cmd.Parameters.Add(_database.CreateParameter("@currencyName", currencyName));
                    cmd.Parameters.Add(_database.CreateParameter("@currencyRatio", currencyRatio));
                    cmd.Parameters.Add(_database.CreateParameter("@exchangeRate", exchangeRate));
                    cmd.Parameters.Add(_database.CreateParameter("@currencyAmount", payment.PayAmount * exchangeRate));
                    cmd.Parameters.Add(_database.CreateParameter("@cashChange", payment.PayAmount - receiptPayPrice));
                    cmd.Parameters.Add(_database.CreateParameter("@cashChangeMainCurrency", (payment.PayAmount - receiptPayPrice) * changeExchangeRate));
                    cmd.Parameters.Add(_database.CreateParameter("@cashChangeMainCurrencyCode", currencyCode));
                    cmd.Parameters.Add(GlobalVar.Instance.SaleDate);
                }
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                throw new POSModuleException("AddPayment", ex.Message, ex);
            }
        }

        public Task CalculateBillAsync(IDbConnection conn, int shopId, int transactionId, int computerId)
        {
            var responseText = "";
            _posModule.OrderDetail_CalBill(ref responseText, transactionId, computerId, shopId,
                GlobalVar.Instance.DecimalDigit, TableSubffix, conn as MySqlConnection);
            if (!string.IsNullOrEmpty(responseText))
                throw new POSModuleException("OrderDetail_CalBill", responseText);
            return Task.FromResult(true);
        }

        public async Task DeletePaymentAsync(IDbConnection conn, int paymentId, int transactionId, int computerId)
        {
            IDbCommand cmd = _database.CreateCommand("delete from orderpaydetailfront " +
                       "where " + (paymentId > 0 ? "PayDetailID=@payDetailId " : "0=0 ") +
                       "and TransactionID=@transactionId and ComputerID=@computerId", conn);
            if (paymentId > 0)
                cmd.Parameters.Add(_database.CreateParameter("@payDetailId", paymentId));
            cmd.Parameters.Add(_database.CreateParameter("@transactionId", transactionId));
            cmd.Parameters.Add(_database.CreateParameter("@computerId", computerId));
            await _database.ExecuteNonQueryAsync(cmd);
        }

        public Task FinalizeAsync(IDbConnection conn, int shopId, int transactionId, int computerId, int staffId, int terminalId)
        {
            var responseText = "";
            var success = _posModule.OrderDetail_FinalizeBill(ref responseText, TableSubffix, transactionId, computerId,
                GlobalVar.Instance.DecimalDigit, staffId, terminalId, conn as MySqlConnection);
            if (!success)
                throw new POSModuleException("OrderDetail_FinalizeBill", responseText);
            success = _posModule.OrderDetail_Final(ref responseText, TableSubffix, transactionId, computerId, shopId,
                GlobalVar.Instance.SaleDateWithBracket, GlobalVar.Instance.DecimalDigit, conn as MySqlConnection);
            if (!success)
                throw new POSModuleException("OrderDetail_Final", responseText);
            return Task.FromResult(success);
        }

        public Task<DataTable> GetOrderDetailAsync(IDbConnection conn, int transactionId, int computerId, int orderDetailId = 0, int langId = 0)
        {
            DataTable dtPromotion = new DataTable();
            DataTable dtBill = new DataTable();
            DataTable dtPayment = new DataTable();
            DataTable dtOrderData = new DataTable();

            string responseText = "";
            bool success = _posModule.GetOrderDetail_View(ref responseText, ref dtOrderData, ref dtPromotion, ref dtBill,
                ref dtPayment, 0, TableSubffix, transactionId, computerId, "ASC", conn as MySqlConnection);
            if (!success)
                throw new POSModuleException("GetOrderDetail_View", responseText);
            return Task.FromResult(dtOrderData);
        }

        public Task OpenTransactionAsync(IDbConnection conn, Transaction transaction)
        {
            var responseText = "";
            var transactionId = 0;
            var computerId = 0;
            var tranKey = "";
            var queueNo = "";
            var isSuccess = _posModule.Tran_Open(ref responseText, ref transactionId, ref computerId, ref tranKey,
               ref queueNo, TableSubffix, transaction.ShopId,
               GlobalVar.Instance.SaleDateWithBracket, transaction.StaffId,
               transaction.ComputerId, transaction.SaleModeId,
               transaction.Name, transaction.QueueName, transaction.NoCustomer,
               transaction.TableId, (int)transaction.Status, conn as MySqlConnection);
            if (isSuccess)
            {
                transaction.TransactionId = transactionId;
                transaction.ComputerId = computerId;
            }
            else
            {
                throw new POSModuleException("Tran_Open", responseText);
            }
            return Task.FromResult(transaction);
        }

        public Task RefreshPromoAsync(IDbConnection conn, int transactionId, int computerId)
        {
            var responseText = "";
            var success = _posModule.OrderDetail_RefreshPromo(ref responseText, TableSubffix,
                transactionId, computerId, GlobalVar.Instance.DecimalDigit, conn as MySqlConnection);
            if (!success)
                throw new POSModuleException("OrderDetail_RefreshPromo", responseText);
            return Task.FromResult(success);
        }
    }
}
