using MySql.Data.MySqlClient;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Web.Http;
using VerticalTec.POS.Database;
using VerticalTec.POS.Service.Ordering.Owin.Exceptions;
using VerticalTec.POS.Service.Ordering.Owin.Models;
using VerticalTec.POS.Service.Ordering.Owin.Services;
using VerticalTec.POS.Utils;
using vtecPOS.GlobalFunctions;

namespace VerticalTec.POS.Service.Ordering.Owin.Controllers
{
    public class PaymentController : ApiController
    {
        static readonly NLog.Logger _log = NLog.LogManager.GetLogger("logpayment");

        IDatabase _database;
        IPaymentService _paymentService;
        IOrderingService _orderingService;
        IMessengerService _messenger;
        IPrintService _printService;
        VtecPOSRepo _posRepo;

        public PaymentController(IDatabase database, IPaymentService paymentService,
            IOrderingService orderingService, IMessengerService messenger,
            IPrintService printService)
        {
            _database = database;
            _paymentService = paymentService;
            _orderingService = orderingService;
            _messenger = messenger;
            _printService = printService;
            _posRepo = new VtecPOSRepo(database);
        }

        [HttpPost]
        [Route("v1/payments/edc")]
        public async Task<IHttpActionResult> EdcPayment(PaymentData paymentData)
        {
            _log.Info("Call v1/payments/edc => {0}", JsonConvert.SerializeObject(paymentData));

            using (var _ = new InvariantCultureScope())
            {
                var result = new HttpActionResult<object>(Request);
                try
                {
                    var respText = "";
                    using (var conn = await _database.ConnectAsync())
                    {
                        string saleDate = await _posRepo.GetSaleDateAsync(conn, paymentData.ShopID, true);
                        var success = false;

                        if (paymentData.EDCType == 0)
                            throw new PaymentException(ErrorCodes.NoPaymentConfig, $"Not found PayType of EDCType {paymentData.EDCType}");

                        var cmd = _database.CreateCommand("", conn);

                        if (paymentData.PayTypeID == 0)
                        {
                            cmd.CommandText = "select PayTypeID from paytype where EDCType=@edcType";
                            cmd.Parameters.Add(_database.CreateParameter("@edcType", paymentData.EDCType));
                            using (IDataReader reader = cmd.ExecuteReader())
                            {
                                if (reader.Read())
                                {
                                    paymentData.PayTypeID = reader.GetValue<int>("PayTypeID");
                                }
                            }
                        }

                        if (paymentData.PayTypeID == 0)
                            throw new PaymentException(ErrorCodes.NoPaymentConfig, $"Not found PayType of EDCType {paymentData.EDCType}");

                        EdcObjLib.objCreditCardInfo cardData = new EdcObjLib.objCreditCardInfo();

                        if (paymentData.EDCType == 22)
                        {
                            var edcPort = paymentData.EDCPort;
                            var timeout = 120;
                            success = EdcObjLib.Mandiri.ClassEdcLib_Mandiri_EDC_CC.SendEdc_CreditCardPayment(paymentData.EDCPort, timeout, paymentData.PayAmount,
                                paymentData.TransactionID, "", "", ref cardData, ref respText);
                        }
                        else if (paymentData.EDCType == 44)
                        {
                            success = EdcObjLib.BCA_V3.ClassEdcLib_BCA_V3_IP_CC.SendEdc_CreditCardPayment(paymentData.EDCIPAddress,
                                paymentData.EDCTcpPort, paymentData.PayAmount, paymentData.TransactionID, "", "", ref cardData, ref respText);
                        }
                        else if (paymentData.EDCType == 48)
                        {
                            var timeout = 120;
                            success = EdcObjLib.Mandiri.ClassEdcLib_Mandiri_EDC_QR.SendEdc_QrCodePayment(paymentData.EDCPort, timeout, paymentData.PayAmount,
                                paymentData.TransactionID, "", "", ref cardData, ref respText);
                        }

                        if (!success)
                        {
                            var errCode = ErrorCodes.EDCCreditPayment;
                            var errMsg = $"Edc credit payment ERROR {respText}";

                            _log.Error(errMsg);
                            throw new PaymentException(errCode, "");
                        }

                        var dtPendingPayment = await _paymentService.GetPendingPaymentAsync(conn, paymentData.TransactionID, paymentData.ComputerID, paymentData.PayTypeID);
                        if (dtPendingPayment.Rows.Count > 0)
                            await _paymentService.DeletePaymentAsync(conn, dtPendingPayment.Rows[0].GetValue<int>("PayDetailID"), paymentData.TransactionID, paymentData.ComputerID);
                        await _paymentService.AddPaymentAsync(conn, paymentData);

                        var posModule = new POSModule();
                        var cardDataJson = JsonConvert.SerializeObject(cardData);
                        _log.Info("EDC Card Info {0}", cardDataJson);

                        success = posModule.Payment_Wallet(ref respText, paymentData.WalletType, cardDataJson, paymentData.TransactionID,
                            paymentData.ComputerID, paymentData.PayDetailID.ToString(), paymentData.ShopID, saleDate, paymentData.BrandName,
                            paymentData.WalletStoreId, paymentData.WalletDeviceId, conn as MySqlConnection);

                        try
                        {
                            if (!string.IsNullOrEmpty(paymentData.TableName))
                            {
                                cmd.CommandText = "update ordertransactionfront set TableName=@tableName, QueueName=@tableName where TransactionID=@transactionId and ComputerID=@computerId";
                                cmd.Parameters.Clear();
                                cmd.Parameters.Add(_database.CreateParameter("@tableName", paymentData.TableName));
                                cmd.Parameters.Add(_database.CreateParameter("@transactionId", paymentData.TransactionID));
                                cmd.Parameters.Add(_database.CreateParameter("@computerId", paymentData.ComputerID));
                                cmd.ExecuteNonQuery();
                            }

                            cmd.CommandText = "update orderpaydetailfront set CreditCardNo=@cardNo, CreditCardHolderName=@cardHolderName, CCApproveCode=@approveCode where TransactionID=@tranId and ComputerID=@compId and PayDetailID=@payDetailId";
                            cmd.Parameters.Clear();
                            cmd.Parameters.Add(_database.CreateParameter("@cardNo", cardData.szCardNo));
                            cmd.Parameters.Add(_database.CreateParameter("@cardHolderName", cardData.szCardHolderName));
                            cmd.Parameters.Add(_database.CreateParameter("@approveCode", cardData.szApprovalCode));
                            cmd.Parameters.Add(_database.CreateParameter("@tranId", paymentData.TransactionID));
                            cmd.Parameters.Add(_database.CreateParameter("@compId", paymentData.ComputerID));
                            cmd.Parameters.Add(_database.CreateParameter("@payDetailId", paymentData.PayDetailID));
                            await _database.ExecuteNonQueryAsync(cmd);
                        }
                        catch (Exception ex)
                        {
                            _log.Error($"{cmd.CommandText} => {ex.Message}");
                        }

                        if (!success)
                        {
                            throw new PaymentException(ErrorCodes.PaymentFunction, respText);
                        }
                        else
                        {
                            await _orderingService.SubmitOrderAsync(conn, paymentData.TransactionID, paymentData.ComputerID, paymentData.ShopID, 0);
                            try
                            {
                                await _paymentService.FinalizeBillAsync(conn, paymentData.TransactionID, paymentData.ComputerID, paymentData.ComputerID, paymentData.ShopID, paymentData.StaffID);
                            }
                            catch (Exception ex)
                            {
                                _log.Error("Error finalize => {0}", ex.Message);
                                _log.Info("Retry finalize bill again");
                                try
                                {
                                    await Task.Delay(1000);
                                    using (var conn2 = await _database.ConnectAsync())
                                    {
                                        await _paymentService.FinalizeBillAsync(conn2, paymentData.TransactionID, paymentData.ComputerID, paymentData.ComputerID, paymentData.ShopID, paymentData.StaffID);
                                    }
                                }
                                catch (Exception ex2)
                                {
                                    _log.Error("Retry finalize bill failed => {0}", ex2.Message);
                                }
                            }

                            try
                            {
                                cmd.CommandText = "update orderpaydetail set CreditCardNo=@cardNo, CreditCardHolderName=@cardHolderName, CCApproveCode=@approveCode where TransactionID=@tranId and ComputerID=@compId and PayDetailID=@payDetailId";
                                await _database.ExecuteNonQueryAsync(cmd);
                            }
                            catch (Exception ex)
                            {
                                _log.Error($"{cmd.CommandText} => {ex.Message}");
                            }


                            _log.Info($"Start printing order trankey: {paymentData.TransactionID}:{paymentData.ComputerID}");
                            await _printService.PrintOrder(new TransactionPayload
                            {
                                TransactionID = paymentData.TransactionID,
                                ComputerID = paymentData.ComputerID,
                                TerminalID = paymentData.ComputerID,
                                ShopID = paymentData.ShopID,
                                LangID = paymentData.LangID,
                                StaffID = paymentData.StaffID,
                                PrinterIds = paymentData.PrinterIds,
                                PrinterNames = paymentData.PrinterNames
                            });

                            _log.Info($"Finished print order trankey: {paymentData.TransactionID}:{paymentData.ComputerID}");


                            _log.Info($"Start printing bill trankey: {paymentData.TransactionID}:{paymentData.ComputerID}");
                            var printData = new PrintData()
                            {
                                TransactionID = paymentData.TransactionID,
                                ComputerID = paymentData.ComputerID,
                                ShopID = paymentData.ShopID,
                                LangID = paymentData.LangID,
                                PrinterIds = paymentData.PrinterIds,
                                PrinterNames = paymentData.PrinterNames,
                                PaperSize = paymentData.PaperSize
                            };
                            await _printService.PrintBill(printData);

                            _log.Info($"Finished print bill trankey: {paymentData.TransactionID}:{paymentData.ComputerID}");
                            _messenger.SendMessage();
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.Message = ex.Message;
                    if (ex is PaymentException apiEx)
                        result.ErrorCode = apiEx.ErrorCode;

                    _log.Error(ex.Message);
                    result.StatusCode = HttpStatusCode.BadRequest;

                    _log.Error("EdcPayment Error => {0}", ex.Message);
                }
                return result;
            }
        }

        #region OnlinePayment
        [HttpPost]
        [Route("v1/payments/online/qr")]
        public async Task<IHttpActionResult> GetQrCodeAsync(PaymentData payment)
        {
            try
            {
                _log.Info($"GetQrCodeAsync Payload={JsonConvert.SerializeObject(payment)}");
            }
            catch { }

            using (var _ = new InvariantCultureScope())
            {
                var result = new HttpActionResult<object>(Request);
                using (var conn = await _database.ConnectAsync())
                {
                    var saleDate = await _posRepo.GetSaleDateAsync(conn, payment.ShopID, false);

                    var cmd = _database.CreateCommand(
                    "select a.ShopKey, a.ShopCode, a.ShopName, b.MerchantKey, c.BrandKey from shop_data a join merchant_data b on a.MerchantID=b.MerchantID join brand_data c on a.MerchantID=c.MerchantID where a.ShopID=@shopId and a.Deleted=0;", conn);

                    cmd.Parameters.Add(_database.CreateParameter("@shopId", payment.ShopID));
                    cmd.Parameters.Add(_database.CreateParameter("@saleDate", saleDate));

                    var ds = new DataSet();
                    var adapter = _database.CreateDataAdapter(cmd);
                    adapter.TableMappings.Add("Table", "ShopData");
                    adapter.Fill(ds);

                    var dtShopData = ds.Tables["ShopData"];

                    if (dtShopData.Rows.Count == 0)
                        throw new VtecPOSException($"Not found shop data {payment.ShopID}");

                    var shopData = dtShopData.AsEnumerable().First();
                    var merchantKey = shopData.GetValue<string>("MerchantKey");
                    var brandKey = shopData.GetValue<string>("BrandKey");
                    var shopKey = shopData.GetValue<string>("ShopKey");
                    var shopCode = shopData.GetValue<string>("ShopCode");
                    var shopName = shopData.GetValue<string>("ShopName");

                    var reqId = Guid.NewGuid().ToString();
                    var reqToken = "";

                    using (var httpClient = new HttpClient())
                    {
                        var baseUrl = await _posRepo.GetPlatformApiAsync(conn);
                        httpClient.BaseAddress = new Uri(baseUrl);

                        var merchantUrl = $"api/MerchantInfo/MerchantInfo?reqId={reqId}&WebUrl={merchantKey}";
                        var propertyUrl = $"api/POSModule/PropertyData?reqId={reqId}";

                        if (string.IsNullOrEmpty(reqToken))
                        {
                            try
                            {
                                reqToken = await _orderingService.GetPlatformApiTokenAsync(httpClient);
                            }
                            catch (Exception ex)
                            {
                                throw new VtecPOSException(ex.Message);
                            }
                        }
                        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", reqToken);

                        var merchantResponse = await httpClient.GetAsync(merchantUrl);
                        if (!merchantResponse.IsSuccessStatusCode)
                            throw new VtecPOSException($"GetMerchant {merchantResponse.ReasonPhrase}");

                        var propertyResponse = await httpClient.PostAsync(propertyUrl, null);
                        if (!propertyResponse.IsSuccessStatusCode)
                            throw new VtecPOSException($"GetProperty {propertyResponse.ReasonPhrase}");

                        var qrPayload = new
                        {
                            shopKey = shopKey,
                            shopID = payment.ShopID,
                            shopCode = shopCode,
                            shopName = shopName,
                            computerID = payment.ComputerID,
                            tranKey = $"{payment.TransactionID}:{payment.ComputerID}",
                            tranUUID = Guid.NewGuid().ToString(),
                            saleDate = saleDate,
                            staffID = payment.StaffID,
                            staffName = "",
                            paymentGatewayType = payment.WalletTypeName,
                            edcType = payment.EDCType,
                            customerCode = payment.CustAccountNo,
                            payAmount = payment.PayAmount.ToString("0.00")
                        };

                        var reqJson = JsonConvert.SerializeObject(qrPayload);
                        var content = new StringContent(reqJson, Encoding.UTF8, "application/json");
                        var resp = await httpClient.PostAsync($"api/POSModule/payment_gateway_QR_Request?req_Id={reqId}&langId=1", content);

                        _log.Info($"Request Gen QR api/POSModule/payment_gateway_QR_Request?req_Id={reqId}&langId=1, ReqJson={reqJson}");

                        try
                        {
                            resp.EnsureSuccessStatusCode();
                            result.StatusCode = HttpStatusCode.OK;

                            var respStr = await resp.Content.ReadAsStringAsync();
                            result.Body = new
                            {
                                platformApi = baseUrl,
                                reqId = reqId,
                                accessToken = reqToken,
                                saleDate = saleDate,
                                reqData = qrPayload,
                                qrData = JsonConvert.DeserializeObject(respStr)
                            };

                            _log.Info($"Resp from api/POSModule/payment_gateway_QR_Request?req_Id={reqId}&langId=1, ReqJson={respStr}");
                        }
                        catch (HttpRequestException ex)
                        {
                            _log.Error($"Gen QR Error api/POSModule/payment_gateway_QR_Request?req_Id={reqId}&langId=1, ReqJson={reqJson}");

                            result.StatusCode = HttpStatusCode.InternalServerError;
                            result.Message = ex.Message;
                        }
                    }
                }
                return result;
            }
        }

        [HttpPost]
        [Route("v1/payments/online/inquiry")]
        public async Task<IHttpActionResult> InquiryAsync(string reqId, string orderId, string tranUUID, PaymentData paymentData)
        {
            try
            {
                _log.Info($"InquiryAsync ReqId={reqId},OrderId={orderId},Payload={JsonConvert.SerializeObject(paymentData)}");
            }
            catch { }

            using (var _ = new InvariantCultureScope())
            {
                var result = new HttpActionResult<object>(Request);
                using (var httpClient = new HttpClient())
                {
                    httpClient.BaseAddress = new Uri(paymentData.PlatformApiUrl);
                    httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", paymentData.Token);

                    var qrPayload = new
                    {
                        shopKey = paymentData.ShopKey,
                        shopID = paymentData.ShopID,
                        shopCode = paymentData.ShopCode,
                        shopName = paymentData.ShopName,
                        computerID = paymentData.ComputerID,
                        tranKey = $"{paymentData.TransactionID}:{paymentData.ComputerID}",
                        tranUUID = tranUUID,
                        saleDate = paymentData.SaleDate,
                        staffID = paymentData.StaffID,
                        staffName = "",
                        paymentGatewayType = paymentData.WalletTypeName,
                        edcType = paymentData.EDCType,
                        customerCode = paymentData.CustAccountNo,
                        payAmount = paymentData.PayAmount.ToString("0.00")
                    };

                    var reqJson = JsonConvert.SerializeObject(qrPayload);

                    _log.Info($"Request QR Inquiry api/POSModule/payment_gateway_QR_Inquiry?reqId={reqId}&orderId={orderId}&langId=1, ReqJson={reqJson}");

                    var content = new StringContent(reqJson, Encoding.UTF8, "application/json");
                    var resp = await httpClient.PostAsync($"api/POSModule/payment_gateway_QR_Inquiry?reqId={reqId}&orderId={orderId}&langId=1", content);

                    var respStr = await resp.Content.ReadAsStringAsync();
                    _log.Info($"Response QR Inquiry api/POSModule/payment_gateway_QR_Inquiry?reqId={reqId}&orderId={orderId}&langId=1, RespJson={respStr}");

                    try
                    {
                        resp.EnsureSuccessStatusCode();
                        result.StatusCode = HttpStatusCode.OK;
                        var apiResp = new
                        {
                            responseCode = "",
                            responseText = "",
                            responseObj = (object)null
                        };

                        apiResp = JsonConvert.DeserializeAnonymousType(respStr, apiResp);
                        if (apiResp.responseCode == "")
                        {
                            result.StatusCode = HttpStatusCode.OK;
                            result.Body = apiResp;

                            using (var conn = await _database.ConnectAsync())
                            {
                                if (paymentData.EDCType != 0)
                                {
                                    var cmd = _database.CreateCommand("select PayTypeID from paytype where EDCType=@edcType", conn);
                                    cmd.Parameters.Add(_database.CreateParameter("@edcType", paymentData.EDCType));
                                    using (IDataReader reader = cmd.ExecuteReader())
                                    {
                                        if (reader.Read())
                                        {
                                            paymentData.PayTypeID = reader.GetValue<int>("PayTypeID");
                                        }
                                    }
                                    if (paymentData.PayTypeID == 0)
                                        throw new VtecPOSException($"Not found PayType of EDCType {paymentData.EDCType}");

                                    var dtPendingPayment = await _paymentService.GetPendingPaymentAsync(conn, paymentData.TransactionID, paymentData.ComputerID, paymentData.PayTypeID);
                                    if (dtPendingPayment.Rows.Count > 0)
                                    {
                                        paymentData.PayDetailID = dtPendingPayment.Rows[0].GetValue<int>("PayDetailID");
                                        await _paymentService.DeletePaymentAsync(conn, paymentData.PayDetailID, paymentData.TransactionID, paymentData.ComputerID);
                                    }

                                    await _paymentService.AddPaymentAsync(conn, paymentData);

                                    var posModule = new POSModule();
                                    var respText = "";
                                    var cardData = new EdcObjLib.objCreditCardInfo()
                                    {
                                        szApprovalCode = apiResp.responseObj?.ToString()
                                    };
                                    var cardDataJson = JsonConvert.SerializeObject(cardData);

                                    var success = posModule.Payment_Wallet(ref respText, paymentData.WalletType, cardDataJson, paymentData.TransactionID,
                                        paymentData.ComputerID, paymentData.PayDetailID.ToString(), paymentData.ShopID, $"'{paymentData.SaleDate}'", paymentData.BrandName,
                                        paymentData.WalletStoreId, paymentData.WalletDeviceId, conn as MySqlConnection);

                                    if (success == false)
                                    {
                                        _log.Error("Payment_Wallet {0}", respText);
                                        throw new VtecPOSException(respText);
                                    }

                                    try
                                    {
                                        if (!string.IsNullOrEmpty(paymentData.TableName))
                                        {
                                            cmd.CommandText = "update ordertransactionfront set TableName=@tableName, QueueName=@tableName where TransactionID=@transactionId and ComputerID=@computerId";
                                            cmd.Parameters.Clear();
                                            cmd.Parameters.Add(_database.CreateParameter("@tableName", paymentData.TableName));
                                            cmd.Parameters.Add(_database.CreateParameter("@transactionId", paymentData.TransactionID));
                                            cmd.Parameters.Add(_database.CreateParameter("@computerId", paymentData.ComputerID));
                                            cmd.ExecuteNonQuery();
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        _log.Error($"{cmd.CommandText} => {ex.Message}");
                                    }

                                    try
                                    {
                                        await _orderingService.SubmitOrderAsync(conn, paymentData.TransactionID, paymentData.ComputerID, paymentData.ShopID, 0);
                                        await _paymentService.FinalizeBillAsync(conn, paymentData.TransactionID, paymentData.ComputerID, paymentData.ComputerID, paymentData.ShopID, paymentData.StaffID);
                                    }
                                    catch (Exception ex)
                                    {
                                        _log.Error(ex, "Error finalize");
                                        _log.Info("Retry finalize bill again");

                                        try
                                        {
                                            await Task.Delay(500);
                                            using (var conn2 = await _database.ConnectAsync())
                                            {
                                                await _paymentService.FinalizeBillAsync(conn2, paymentData.TransactionID, paymentData.ComputerID, paymentData.ComputerID, paymentData.ShopID, paymentData.StaffID);
                                            }
                                        }
                                        catch (Exception ex2)
                                        {
                                            _log.Error(ex2, "Retry finalize bill failed");
                                        }
                                    }

                                    _log.Info($"Start printing order trankey: {paymentData.TransactionID}:{paymentData.ComputerID}");
                                    await _printService.PrintOrder(new TransactionPayload
                                    {
                                        TransactionID = paymentData.TransactionID,
                                        ComputerID = paymentData.ComputerID,
                                        TerminalID = paymentData.ComputerID,
                                        ShopID = paymentData.ShopID,
                                        LangID = paymentData.LangID,
                                        StaffID = paymentData.StaffID,
                                        PrinterIds = paymentData.PrinterIds,
                                        PrinterNames = paymentData.PrinterNames
                                    });
                                    _log.Info($"Finished print order trankey: {paymentData.TransactionID}:{paymentData.ComputerID}");


                                    _log.Info($"Start printing bill trankey: {paymentData.TransactionID}:{paymentData.ComputerID}");

                                    var printData = new PrintData()
                                    {
                                        TransactionID = paymentData.TransactionID,
                                        ComputerID = paymentData.ComputerID,
                                        ShopID = paymentData.ShopID,
                                        LangID = paymentData.LangID,
                                        PrinterIds = paymentData.PrinterIds,
                                        PrinterNames = paymentData.PrinterNames,
                                        PaperSize = paymentData.PaperSize
                                    };

                                    await _printService.PrintBill(printData);

                                    _log.Info($"Finished print bill trankey: {paymentData.TransactionID}:{paymentData.ComputerID}");

                                    _messenger.SendMessage();
                                }
                            }
                        }
                        else
                        {
                            if (apiResp.responseCode == "99")
                            {
                                if (paymentData.EDCType == 47)
                                {
                                    if (apiResp.responseText?.Contains("Response status code does not indicate success: 401 (Unauthorized).") == true)
                                    {
                                        apiResp = new
                                        {
                                            responseCode = "98",
                                            responseText = apiResp.responseText,
                                            responseObj = apiResp.responseObj
                                        };
                                    }
                                }
                                _log.Error($"Inquiry api/POSModule/payment_gateway_QR_Inquiry?reqId={reqId}&orderId={orderId}&langId=1, Error {apiResp.responseText}, reqId={reqId}, reqJson={reqJson}");
                            }
                            result.StatusCode = HttpStatusCode.OK;
                            result.Body = apiResp;
                        }
                    }
                    catch (Exception ex)
                    {
                        result.StatusCode = HttpStatusCode.InternalServerError;
                        result.Message = ex.Message;

                        _log.Error($"Inquiry api/POSModule/payment_gateway_QR_Inquiry?reqId={reqId}&orderId={orderId}&langId=1, Error {ex.Message}, reqId={reqId}, reqJson={reqJson}");
                    }
                }
                return result;
            }
        }
        #endregion

        [HttpGet]
        [Route("v1/payments/checkedctype")]
        public async Task<IHttpActionResult> CheckEDCTypeAsync(int edcType)
        {
            var result = new HttpActionResult<object>(Request);
            using (var conn = await _database.ConnectAsync())
            {
                bool isAlreadyHaveEdcType = false;
                var cmd = _database.CreateCommand("select PayTypeID from paytype where EDCType=@edcType", conn);
                cmd.Parameters.Add(_database.CreateParameter("@edcType", edcType));
                using (var reader = await _database.ExecuteReaderAsync(cmd))
                {
                    if (reader.Read())
                    {
                        isAlreadyHaveEdcType = true;
                    }
                }
                if (isAlreadyHaveEdcType)
                {
                    result.StatusCode = HttpStatusCode.OK;
                }
                else
                {
                    result.StatusCode = HttpStatusCode.NotFound;
                }
            }
            return result;
        }

        [HttpPost]
        [Route("v1/payments/add")]
        public async Task<IHttpActionResult> AddPaymentDetailAsync(PaymentData paymentData)
        {
            _log.Info($"AddPayment => {JsonConvert.SerializeObject(paymentData)}");

            var result = new HttpActionResult<PaymentData>(Request);
            using (var conn = await _database.ConnectAsync())
            {
                await _paymentService.AddPaymentAsync(conn, paymentData);
                result.StatusCode = HttpStatusCode.OK;
                result.Body = paymentData;
            }
            return result;
        }

        [HttpPost]
        [Route("v1/payments/del")]
        public async Task<IHttpActionResult> DeletePaymentDetailAsync(int transactionId, int computerId, int payDetailId)
        {
            var result = new HttpActionResult<string>(Request);
            using (var conn = await _database.ConnectAsync())
            {
                await _paymentService.DeletePaymentAsync(conn, transactionId, computerId, payDetailId);
                result.StatusCode = HttpStatusCode.OK;
            }
            return result;
        }

        [HttpGet]
        [Route("v1/payments/details")]
        public async Task<IHttpActionResult> GetPaymentDetailAsync(int transactionId, int computerId)
        {
            var result = new HttpActionResult<List<PaymentData>>(Request);
            List<PaymentData> payDetailList = new List<PaymentData>();
            using (var conn = await _database.ConnectAsync())
            {
                DataTable dtPayDetail = await _paymentService.GetPaymentDetailAsync(conn, transactionId, computerId);
                foreach (DataRow row in dtPayDetail.Rows)
                {
                    payDetailList.Add(new PaymentData()
                    {
                        TransactionID = row.GetValue<int>("TransactionID"),
                        ComputerID = row.GetValue<int>("ComputerID"),
                        PayDetailID = row.GetValue<int>("PayDetailID"),
                        PayTypeID = row.GetValue<int>("PayTypeID"),
                        PayAmount = row.GetValue<decimal>("PayAmount"),
                        CurrencyAmount = row.GetValue<decimal>("CurrencyAmount"),
                        PayTypeName = row.GetValue<string>("PayTypeName")
                    });
                }
                result.Body = payDetailList;
                result.StatusCode = HttpStatusCode.OK;
            }
            return result;
        }

        [HttpGet]
        [Route("v1/payments")]
        public async Task<IHttpActionResult> GetPaymentDataAsync(int computerId, int langId = 1, int currencyId = 1)
        {
            var result = new HttpActionResult<DataSet>(Request);
            using (IDbConnection conn = await _database.ConnectAsync())
            {
                try
                {
                    DataSet dataSet = await _paymentService.GetPaymentDataAsync(conn, computerId, langId, currencyId);

                    result.StatusCode = HttpStatusCode.OK;
                    result.Body = dataSet;
                }
                catch (VtecPOSException ex)
                {
                    result.StatusCode = HttpStatusCode.InternalServerError;
                    result.Message = ex.Message;
                }
            }
            return result;
        }

        [HttpGet]
        [Route("v1/payments/currencies")]
        public async Task<IHttpActionResult> GetPaymentCurrencyAsync()
        {
            var result = new HttpActionResult<DataTable>(Request);
            using (IDbConnection conn = await _database.ConnectAsync())
            {
                DataTable dtPaymentCurrency = await _paymentService.GetPaymentCurrencyAsync(conn);
                result.StatusCode = HttpStatusCode.OK;
                result.Body = dtPaymentCurrency;
            }
            return result;
        }

        [HttpPost]
        [Route("v1/payments/finalize")]
        public async Task<IHttpActionResult> FinalizeBillAsync(PaymentData payload)
        {
            _log.Info($"FinalizeBill => {JsonConvert.SerializeObject(payload)}");

            var result = new HttpActionResult<string>(Request);
            using (var conn = await _database.ConnectAsync())
            {
                try
                {
                    await _paymentService.FinalizeBillAsync(conn, payload.TransactionID, payload.ComputerID,
                        payload.TerminalID, payload.ShopID, payload.StaffID);
                    await _paymentService.FinalizeOrderAsync(conn, payload.TransactionID, payload.ComputerID,
                        payload.TerminalID, payload.ShopID, payload.StaffID, payload.LangID, payload.PrinterIds, payload.PrinterNames);

                    var printData = new PrintData()
                    {
                        TransactionID = payload.TransactionID,
                        ComputerID = payload.ComputerID,
                        ShopID = payload.ShopID,
                        LangID = payload.LangID,
                        PrinterIds = payload.PrinterIds,
                        PrinterNames = payload.PrinterNames,
                        PaperSize = payload.PaperSize
                    };
                    //BackgroundJob.Enqueue<IPrintService>(p => p.PrintBill(printData));
                    await _printService.PrintBill(printData);
                    _messenger.SendMessage();
                }
                catch (VtecPOSException ex)
                {
                    result.StatusCode = HttpStatusCode.InternalServerError;
                    result.Message = ex.Message;
                }
            }
            return result;
        }

        [HttpPost]
        [Route("v1/payments/kiosk/del")]
        public async Task<IHttpActionResult> KioskDelPaymentAsync(PaymentData paymentData)
        {
            var result = new HttpActionResult<object>(Request);
            using (var conn = await _database.ConnectAsync())
            {
                var responseText = "";
                var receiptString = "";
                var saleDate = $"'{await _posRepo.GetSaleDateAsync(conn, paymentData.ShopID, false)}'";
                var decimalDigit = await _posRepo.GetDefaultDecimalDigitAsync(conn);

                if (paymentData.EDCType != 0)
                {
                    var cmd = _database.CreateCommand("select PayTypeID from paytype where EDCType=@edcType", conn);
                    cmd.Parameters.Add(_database.CreateParameter("@edcType", paymentData.EDCType));
                    using (var reader = await _database.ExecuteReaderAsync(cmd))
                    {
                        if (reader.Read())
                        {
                            paymentData.PayTypeID = reader.GetValue<int>("PayTypeID");
                        }
                    }
                }

                if (paymentData.PayTypeID == 0)
                {
                    result.StatusCode = HttpStatusCode.InternalServerError;
                    result.Message = $"Not found PayType of EDCType {paymentData.EDCType}";
                }
                else
                {
                    var posModule = new POSModule();
                    var success = posModule.Payment_Del(ref responseText, ref receiptString, paymentData.TransactionID, paymentData.ComputerID, paymentData.PayTypeID,
                        paymentData.ShopID, saleDate, paymentData.TerminalID, paymentData.StaffID, paymentData.LangID, "front", decimalDigit, conn as MySqlConnection);
                    if (!success)
                    {
                        result.StatusCode = HttpStatusCode.InternalServerError;
                        result.Message = responseText;
                    }
                }
            }
            return result;
        }

        [HttpPost]
        [Route("v1/payments/kiosk/add")]
        public async Task<IHttpActionResult> KioskAddPayment(PaymentData paymentData)
        {
            var result = new HttpActionResult<object>(Request);
            using (var conn = await _database.ConnectAsync())
            {
                var responseText = "";
                var tranDataSet = new DataSet();
                var receiptString = "";
                var saleDate = $"'{await _posRepo.GetSaleDateAsync(conn, paymentData.ShopID, false)}'";
                var decimalDigit = await _posRepo.GetDefaultDecimalDigitAsync(conn);

                if (paymentData.EDCType != 0)
                {
                    var cmd = _database.CreateCommand("select PayTypeID from paytype where EDCType=@edcType", conn);
                    cmd.Parameters.Add(_database.CreateParameter("@edcType", paymentData.EDCType));
                    using (var reader = await _database.ExecuteReaderAsync(cmd))
                    {
                        if (reader.Read())
                        {
                            paymentData.PayTypeID = reader.GetValue<int>("PayTypeID");
                        }
                    }
                }

                if (paymentData.PayTypeID == 0)
                {
                    result.StatusCode = HttpStatusCode.InternalServerError;
                    result.Message = $"Not found PayType of EDCType {paymentData.EDCType}";
                }
                else
                {
                    var posModule = new POSModule();
                    var success = posModule.Payment_Add(ref responseText, ref tranDataSet, ref receiptString, paymentData.TransactionID, paymentData.ComputerID,
                        paymentData.PayTypeID, paymentData.ShopID, saleDate, paymentData.TerminalID, paymentData.StaffID, paymentData.LangID, "front", decimalDigit, conn as MySqlConnection);
                    if (!success)
                    {
                        result.StatusCode = HttpStatusCode.InternalServerError;
                        result.Message = responseText;
                    }
                    result.Body = tranDataSet;
                }
            }
            return result;
        }

        [HttpPost]
        [Route("v1/payments/kiosk/confirm")]
        public async Task<IHttpActionResult> KioskConfirmPayment(PaymentData payload)
        {
            var result = new HttpActionResult<PaymentData>(Request);

            using (var conn = await _database.ConnectAsync())
            {
                try
                {
                    await ConfirmKioskPaymentAsync(conn, payload);
                }
                catch (VtecPOSException ex)
                {
                    result.StatusCode = HttpStatusCode.InternalServerError;
                    result.Message = ex.Message;
                }
            }
            return result;
        }

        [HttpPost]
        [Route("v1/payments/grc")]
        public async Task<IHttpActionResult> GrcPaymentGateway(PaymentData payload)
        {
            var result = new HttpActionResult<object>(Request);
            var baseUrl = "";
            var reqTime = DateTime.Now;
            using (var conn = await _database.ConnectAsync())
            {
                baseUrl = await _posRepo.GetPropertyValueAsync(conn, 1117, "BaseUrl");

                var cmd = _database.CreateCommand("update ordertransactionfront set PaidTime=@reqTime where TransactionID=@tranId and ComputerID=@compId", conn);
                cmd.Parameters.Add(_database.CreateParameter("@reqTime", reqTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
                cmd.Parameters.Add(_database.CreateParameter("@tranId", payload.TransactionID));
                cmd.Parameters.Add(_database.CreateParameter("@compId", payload.ComputerID));
                await _database.ExecuteNonQueryAsync(cmd);

                if (!string.IsNullOrEmpty(baseUrl))
                {
                    var httpClient = new HttpClient();
                    httpClient.Timeout = TimeSpan.FromSeconds(90);

                    var fintech = "";
                    if (payload.EDCType == 39)
                        fintech = "OVO";

                    var grcPayload = new GrcPayload
                    {
                        OrderID = $"{payload.ReferenceNo}{reqTime.ToString("HHmmss")}",
                        ShopID = payload.ShopID,
                        Amount = Convert.ToInt32(payload.PayAmount),
                        ComputerID = payload.ComputerID,
                        Fintech = fintech,
                        CustomerAccount = payload.CustAccountNo
                    };

                    if (!baseUrl.EndsWith("/"))
                        baseUrl = baseUrl + "/";

                    var grc = new GrcPaymentData();
                    try
                    {
                        _log.Info($"Request GrcPaymentGateway: {JsonConvert.SerializeObject(grcPayload)}");

                        var uri = new UriBuilder($"{baseUrl}pay").ToString();
                        var json = JsonConvert.SerializeObject(grcPayload);
                        var reqContent = new StringContent(json);
                        reqContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                        var resp = await httpClient.PostAsync(uri, reqContent);
                        if (resp.IsSuccessStatusCode)
                        {
                            var respContent = await resp.Content.ReadAsStringAsync();
                            grc = JsonConvert.DeserializeObject<GrcPaymentData>(respContent);

                            _log.Info($"Response from {uri}: {JsonConvert.SerializeObject(grc)}");

                            if (grc.response_code == "00")
                            {
                                var edcObject = new EdcObjLib.objCreditCardInfo()
                                {
                                    szTransactionDate = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                                    szTransactionTime = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                                    szCustAddress = $"{grc.store?.address1} {grc.store?.address2}",
                                    szBalanceAmount = grc.account?.cash_balance,
                                    szCustName = grc.account?.full_name,
                                    szRedeemPoint = grc.account?.ovo_points_used,
                                    szRedeemPointBalance = grc.account?.points_balance,
                                    szApprovalCode = "",
                                    szMerchantNo = "",
                                    szReferenceNo = "",
                                    szAmount = payload.PayAmount.ToString()
                                };
                                payload.ExtraParam = JsonConvert.SerializeObject(edcObject);

                                var tran = new TransactionPayload()
                                {
                                    ShopID = payload.ShopID,
                                    TransactionID = payload.TransactionID,
                                    ComputerID = payload.ComputerID,
                                    TerminalID = payload.ComputerID,
                                    StaffID = payload.StaffID,
                                    PrinterIds = payload.PrinterIds,
                                    PrinterNames = payload.PrinterNames
                                };

                                await _orderingService.SubmitOrderAsync(conn, payload.TransactionID, payload.ComputerID, payload.ShopID, payload.TableID);
                                await _printService.PrintOrder(tran);

                                await ConfirmKioskPaymentAsync(conn, payload);
                                result.Body = grc;
                            }
                            else
                            {
                                var errMsg = $"{grc.response_message}\n{grc.response_description}";
                                if (string.IsNullOrEmpty(grc.response_message))
                                    errMsg = "Unknown error!";

                                result.StatusCode = HttpStatusCode.InternalServerError;
                                result.Message = errMsg;
                                _log.Error($"{baseUrl} Fail: {result.Message}");
                            }
                        }
                        else
                        {
                            result.StatusCode = HttpStatusCode.InternalServerError;
                            result.Message = resp.ReasonPhrase;
                            _log.Error($"{baseUrl} Fail: {result.Message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        var message = ex.Message;
                        if (ex is TaskCanceledException)
                        {
                            message = $"Can't connect to {baseUrl} {ex.Message}";
                            result.StatusCode = HttpStatusCode.GatewayTimeout;
                        }
                        result.Message = message;
                        _log.Error(message);
                    }
                }
                else
                {
                    result.StatusCode = HttpStatusCode.InternalServerError;
                    result.Message = "Not found GRC Payment Gateway base url (prop 1117)";

                    _log.Error("Not found GRC Payment Gateway base url (prop 1117)");
                }
            }
            return result;
        }

        [HttpPost]
        [Route("v1/payments/grc/check")]
        public async Task<IHttpActionResult> CheckGrcPayment(PaymentData payload)
        {
            var result = new HttpActionResult<object>(Request);

            var baseUrl = "";
            var orderId = "";
            using (var conn = await _database.ConnectAsync())
            {
                baseUrl = await _posRepo.GetPropertyValueAsync(conn, 1117, "BaseUrl");

                var cmd = _database.CreateCommand("select ReferenceNo, PaidTime from ordertransactionfront where TransactionID=@tranId and ComputerID=@compId", conn);
                cmd.Parameters.Add(_database.CreateParameter("@tranId", payload.TransactionID));
                cmd.Parameters.Add(_database.CreateParameter("@compId", payload.ComputerID));
                using (var reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        orderId = $"{reader.GetValue<string>("ReferenceNo")}{reader.GetValue<DateTime>("PaidTime").ToString("HHmmss")}";
                    }
                }
                if (!string.IsNullOrEmpty(baseUrl))
                {
                    var httpClient = new HttpClient();
                    httpClient.Timeout = TimeSpan.FromSeconds(60);

                    if (!baseUrl.EndsWith("/"))
                        baseUrl = baseUrl + "/";

                    var grc = new GrcPaymentData();
                    try
                    {
                        var fintech = "";
                        if (payload.EDCType == 39)
                            fintech = "ovo";

                        var uri = new UriBuilder($"{baseUrl}check/{fintech}/{orderId}").ToString();
                        _log.Info($"Request GrcPaymentGateway for CheckPayment: {uri}");

                        var resp = await httpClient.GetAsync(uri);
                        if (resp.IsSuccessStatusCode)
                        {
                            var respContent = await resp.Content.ReadAsStringAsync();
                            grc = JsonConvert.DeserializeObject<GrcPaymentData>(respContent);

                            _log.Info($"Response from {uri}: {JsonConvert.SerializeObject(grc)}");

                            if (grc.response_code == "00")
                            {
                                var edcObject = new EdcObjLib.objCreditCardInfo()
                                {
                                    szTransactionDate = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                                    szTransactionTime = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                                    szCustAddress = $"{grc.store?.address1} {grc.store?.address2}",
                                    szBalanceAmount = grc.account?.cash_balance,
                                    szCustName = grc.account?.full_name,
                                    szRedeemPoint = grc.account?.ovo_points_used,
                                    szRedeemPointBalance = grc.account?.points_balance,
                                    szApprovalCode = "",
                                    szMerchantNo = "",
                                    szReferenceNo = "",
                                    szAmount = payload.PayAmount.ToString()
                                };
                                payload.ExtraParam = JsonConvert.SerializeObject(edcObject);

                                var tran = new TransactionPayload()
                                {
                                    ShopID = payload.ShopID,
                                    TransactionID = payload.TransactionID,
                                    ComputerID = payload.ComputerID,
                                    TerminalID = payload.ComputerID,
                                    StaffID = payload.StaffID,
                                    PrinterIds = payload.PrinterIds,
                                    PrinterNames = payload.PrinterNames
                                };

                                await _orderingService.SubmitOrderAsync(conn, payload.TransactionID, payload.ComputerID, payload.ShopID, payload.TableID);
                                await _printService.PrintOrder(tran);

                                await ConfirmKioskPaymentAsync(conn, payload);
                                result.Body = grc;
                            }
                            else
                            {
                                var errMsg = $"{grc.response_message}\n{grc.response_description}";
                                if (string.IsNullOrEmpty(grc.response_message))
                                    errMsg = "Unknow error!";

                                result.StatusCode = HttpStatusCode.InternalServerError;
                                result.Message = errMsg;

                                _log.Error($"{baseUrl} Fail: {errMsg}");
                            }
                        }
                        else
                        {
                            result.StatusCode = HttpStatusCode.InternalServerError;
                            result.Message = resp.ReasonPhrase;

                            _log.Error($"{baseUrl} Fail: {result.Message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        var message = ex.Message;
                        if (ex is TaskCanceledException)
                        {
                            message = $"Can't connect to {baseUrl} {ex.Message}";
                            result.StatusCode = HttpStatusCode.GatewayTimeout;
                            _log.Error(message);
                        }
                        result.Message = message;
                    }
                }
                else
                {
                    result.StatusCode = HttpStatusCode.InternalServerError;
                    result.Message = "Not found GRC Payment Gateway base url (prop 1117)";

                    _log.Error("Not found GRC Payment Gateway base url (prop 1117)");
                }
            }
            return result;
        }

        async Task ConfirmKioskPaymentAsync(IDbConnection conn, PaymentData payload)
        {
            string saleDate = await _posRepo.GetSaleDateAsync(conn, payload.ShopID, true);

            string responseText = "";

            if (!string.IsNullOrEmpty(payload.TableName))
            {
                var cmd = _database.CreateCommand("update ordertransactionfront set QueueName=@tableName " +
                    " where TransactionID=@transactionId and ComputerID=@computerId", conn);
                cmd.Parameters.Add(_database.CreateParameter("@tableName", payload.TableName));
                cmd.Parameters.Add(_database.CreateParameter("@transactionId", payload.TransactionID));
                cmd.Parameters.Add(_database.CreateParameter("@computerId", payload.ComputerID));
                await _database.ExecuteNonQueryAsync(cmd);
            }

            if (payload.EDCType != 0)
            {
                var cmd = _database.CreateCommand("select PayTypeID from paytype where EDCType=@edcType", conn);
                cmd.Parameters.Add(_database.CreateParameter("@edcType", payload.EDCType));
                using (IDataReader reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        payload.PayTypeID = reader.GetValue<int>("PayTypeID");
                    }
                }
                if (payload.PayTypeID == 0)
                    throw new VtecPOSException($"Not found PayType of EDCType {payload.EDCType}");
            }

            var dtPendingPayment = await _paymentService.GetPendingPaymentAsync(conn, payload.TransactionID, payload.ComputerID, payload.PayTypeID);
            if (dtPendingPayment.Rows.Count > 0)
                await _paymentService.DeletePaymentAsync(conn, dtPendingPayment.Rows[0].GetValue<int>("PayDetailID"), payload.TransactionID, payload.ComputerID);
            await _paymentService.AddPaymentAsync(conn, payload);

            var posModule = new POSModule();
            var isSuccess = posModule.Payment_Wallet(ref responseText, payload.WalletType, payload.ExtraParam, payload.TransactionID,
                payload.ComputerID, payload.PayDetailID.ToString(), payload.ShopID, saleDate, payload.BrandName,
                payload.WalletStoreId, payload.WalletDeviceId, conn as MySqlConnection);

            if (isSuccess)
            {
                await _paymentService.FinalizeBillAsync(conn, payload.TransactionID, payload.ComputerID, payload.TerminalID, payload.ShopID, payload.StaffID);
                await _paymentService.FinalizeOrderAsync(conn, payload.TransactionID, payload.ComputerID, payload.TerminalID,
                    payload.ShopID, payload.StaffID, payload.LangID, payload.PrinterIds, payload.PrinterNames);

                var printData = new PrintData()
                {
                    TransactionID = payload.TransactionID,
                    ComputerID = payload.ComputerID,
                    ShopID = payload.ShopID,
                    LangID = payload.LangID,
                    PrinterIds = payload.PrinterIds,
                    PrinterNames = payload.PrinterNames,
                    PaperSize = payload.PaperSize
                };

                await _printService.PrintBill(printData);
                _messenger.SendMessage();
            }
            else
            {
                throw new VtecPOSException(responseText);
            }
        }
    }
}