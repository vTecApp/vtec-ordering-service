using MySql.Data.MySqlClient;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using VerticalTec.POS.Database;
using VerticalTec.POS.Service.Ordering.Owin.Models;
using VerticalTec.POS.Utils;
using vtecPOS.GlobalFunctions;
using vtecPOS.POSControl;

namespace VerticalTec.POS.Service.Ordering.Owin.Services
{
    public class PrintService : IPrintService
    {
        static readonly object lockPrint = new object();
        static readonly NLog.Logger _log = NLog.LogManager.GetLogger("logordering");

        IDatabase _db;
        IOrderingService _orderingService;
        VtecPOSRepo _posRepo;

        public PrintService(IDatabase database, IOrderingService orderingService)
        {
            _db = database;
            _orderingService = orderingService;
            _posRepo = new VtecPOSRepo(database);
        }

        public Task<bool> PrintOrder(TransactionPayload payload)
        {
            lock (lockPrint)
            {
                using (var conn = _db.ConnectAsync().Result)
                {
                    var myConn = conn as MySqlConnection;
                    var posModule = new POSModule();
                    int defaultDecimalDigit = _posRepo.GetDefaultDecimalDigitAsync(conn).Result;
                    string saleDate = _posRepo.GetSaleDateAsync(conn, payload.ShopID, true).Result;
                    DataSet dsSummaryData = new DataSet();
                    DataSet dsSummaryOrderData = new DataSet();
                    DataSet dsOrderData = new DataSet();
                    var responseText = "";

                    var isSuccess = posModule.Summary_Print(ref responseText, ref dsSummaryData, "front", payload.ShopID, saleDate,
                            payload.TransactionID, payload.ComputerID, payload.StaffID, payload.TerminalID, myConn);

                    _log.Info("DataSet from Summary_Print => {0}", JsonConvert.SerializeObject(dsSummaryData));

                    if (!isSuccess)
                    {
                        _log.Error("Call Summary_Print => {0}", responseText);
                    }

                    var isSummaryPrint = _posRepo.GetPropertyValueAsync(conn, 1009, "SummaryPrint").Result == "1";
                    if (isSummaryPrint)
                    {
                        var receiptStr = "";
                        var copyStr = "";
                        var noCopy = 0;
                        var dsBillDetail = new DataSet();

                        isSuccess = posModule.BillDetail(ref responseText, ref receiptStr, ref copyStr, ref noCopy, ref dsBillDetail, 1, payload.TransactionID, payload.ComputerID, "front", payload.LangID, myConn);
                        if (isSuccess)
                        {
                            try
                            {
                                using (var conn2 = _db.ConnectAsync().Result)
                                {
                                    CDBUtil dbUtil = new CDBUtil();
                                    PrintingObjLib.PrintLib.ThreadPrintKitchenDatatFromDataSet(conn2 as MySqlConnection, dbUtil, posModule, payload.ShopID, payload.ComputerID, dsBillDetail);
                                }
                            }
                            catch (Exception ex)
                            {
                                _log.Error("Call ThreadPrintKitchenDatatFromDataSet => {0}", ex.Message);
                            }
                        }
                        else
                        {
                            _log.Error("Call BillDetail => {0}", responseText);
                        }
                    }

                    int batchId = 0;
                    isSuccess = posModule.Table_PrintSummaryOrder(ref responseText, ref batchId, "front", payload.PrinterIds,
                        payload.TransactionID, payload.ComputerID, payload.ShopID, saleDate, payload.StaffID,
                        payload.TerminalID, payload.TableID, payload.LangID, defaultDecimalDigit, myConn);
                    if (isSuccess)
                    {
                        isSuccess = posModule.Table_PrintSummaryOrderData(ref responseText, ref dsSummaryOrderData, batchId, "front",
                            payload.TransactionID, payload.ComputerID, payload.ShopID, saleDate, payload.LangID, myConn);
                        if (!isSuccess)
                        {
                            _log.Error($"Call Table_PrintSummaryOrderData {responseText}");
                        }
                    }
                    else
                    {
                        _log.Error($"Call PrintSummaryOrder {responseText}");
                    }

                    batchId = 0;
                    isSuccess = posModule.Table_PrintOrder(ref responseText, ref batchId, "front", payload.TransactionID,
                        payload.ComputerID, payload.ShopID, saleDate, payload.StaffID,
                        payload.TerminalID, payload.TableID, payload.LangID, defaultDecimalDigit, myConn);
                    if (isSuccess)
                    {
                        isSuccess = posModule.Table_PrintOrderData(ref responseText, ref dsOrderData, batchId, "front",
                            payload.TransactionID, payload.ComputerID, payload.ShopID, saleDate, payload.LangID, myConn);
                        if (!isSuccess)
                        {
                            _log.Error($"Call Table_PrintOrderData {responseText}");
                        }
                    }
                    else
                    {
                        _log.Error($"Call Table_PrintOrder Table_PrintOrder {responseText}");
                    }

                    posModule.Table_UpdateStatus(ref responseText, "front", payload.TransactionID, payload.ComputerID,
                        payload.ShopID, saleDate, payload.LangID, myConn);

                    try
                    {
                        CDBUtil dbUtil = new CDBUtil();
                        PrintingObjLib.PrintLib.PrintKdsDataFromDataSet(myConn, dbUtil, posModule, payload.ShopID, payload.ComputerID, dsSummaryData);
                        PrintingObjLib.PrintLib.PrintKdsDataFromDataSet(myConn, dbUtil, posModule, payload.ShopID, payload.ComputerID, dsSummaryOrderData);
                        PrintingObjLib.PrintLib.PrintKdsDataFromDataSet(myConn, dbUtil, posModule, payload.ShopID, payload.ComputerID, dsOrderData);
                    }
                    catch (Exception ex)
                    {
                        _log.Error("PrintKdsDataFromDataSet => {0}", ex.Message);
                    }
                }
            }
            return Task.FromResult(true);
        }

        public async Task PrintBill(PrintData payload)
        {
            using (var conn = await _db.ConnectAsync())
            {
                try
                {
                    var dsPrintData = await _orderingService.GetBillDetail(conn, payload.TransactionID, payload.ComputerID, payload.ShopID, payload.LangID);
                    var printerNames = new List<string>();
                    try
                    {
                        printerNames = payload.PrinterNames.Split(',').ToList();
                    }
                    catch
                    {
                        printerNames = new List<string>
                        {
                            payload.PrinterNames
                        };
                    }
                    foreach (var printerName in printerNames)
                    {
                        await PrintAsync(payload.ShopID, payload.ComputerID, payload.PrinterIds, printerName, dsPrintData, payload.PaperSize);
                    }
                }
                catch (Exception ex)
                {
                    _log.Error("GetBillDetail {0}", ex.Message);
                }
            }
        }

        public async Task PrintCheckBill(TransactionPayload payload)
        {
            using (var conn = await _db.ConnectAsync())
            {
                try
                {
                    var dsPrintData = await _orderingService.CheckBillAsync(conn, payload.TransactionID, payload.ComputerID,
                        payload.ShopID, payload.TerminalID, payload.StaffID, payload.LangID);
                    await PrintAsync(payload.ShopID, payload.ComputerID, payload.PrinterIds, payload.PrinterNames, dsPrintData, 80);

                    await _orderingService.UpdateTableStatusAsync(conn, payload.TransactionID, payload.ComputerID, payload.ShopID, payload.LangID);
                }
                catch (Exception ex)
                {
                    _log.Error(ex.Message);
                }
            }
        }

        public async Task KioskPrintCheckBill(TransactionPayload payload)
        {
            using (var conn = await _db.ConnectAsync())
            {
                try
                {
                    var dsPrintData = await _orderingService.CheckBillAsync(conn, payload.TransactionID, payload.ComputerID,
                        payload.ShopID, payload.TerminalID, payload.StaffID, payload.LangID, true);

                    var printerNames = new List<string>();
                    try
                    {
                        printerNames = payload.PrinterNames.Split(',').ToList();
                    }
                    catch
                    {
                        printerNames = new List<string>
                        {
                            payload.PrinterNames
                        };
                    }
                    foreach (var printerName in printerNames)
                    {
                        await PrintAsync(payload.ShopID, payload.ComputerID, payload.PrinterIds, printerName, dsPrintData, 80);
                    }
                    await _orderingService.UpdateTableStatusAsync(conn, payload.TransactionID, payload.ComputerID, payload.ShopID, payload.LangID);
                }
                catch (Exception ex)
                {
                    _log.Error(ex.Message);
                }
            }
        }

        public async Task PrintAsync(int shopId, int computerId, DataSet dsPrintData)
        {
            if (dsPrintData.Tables.Count > 0)
            {
                using (var conn = await _db.ConnectAsync())
                {
                    var posModule = new POSModule();
                    CDBUtil dbUtil = new CDBUtil();
                    PrintingObjLib.PrintLib.PrintKdsDataFromDataSet(conn as MySqlConnection, dbUtil, posModule,
                        shopId, computerId, dsPrintData);
                }
            }
        }

        public async Task PrintAsync(int shopId, int computerId, string printerIds, string printerNames, DataSet dsPrintData, int paperSize = 80)
        {
            try
            {
                IPAddress ip;
                var isIPFormat = IPAddress.TryParse(printerNames, out ip);
                if (isIPFormat)
                {
                    Device.Printer.Epson.EpsonResponse response = null;
                    var size = Device.Printer.Epson.PaperSizes.Size80;
                    if (paperSize == 58)
                        size = Device.Printer.Epson.PaperSizes.Size58;
                    response = await Device.Printer.Epson.EpsonPrintManager.Instance.PrintBillDetail(
                        dsPrintData, printerIds, printerNames, size);
                    if (response != null && response.Success == false)
                        _log.Error($"Printer error => {response.Message}");
                }
                else
                {
                    var dbServer = AppConfig.Instance.DbServer;
                    var dbName = AppConfig.Instance.DbName;
                    var dbPort = AppConfig.Instance.DbPort;

                    var posModule = new POSModule();
                    if (!string.IsNullOrEmpty(printerNames))
                    {
                        _log.Info($"Print to {printerNames}");

                        PrintingObjLib.PrintLib.PrintDataFromDataSet(posModule, shopId,
                            computerId, dsPrintData, printerNames, dbServer, dbName, "3308");
                    }
                    else
                    {
                        PrintingObjLib.PrintLib.PrintDataFromDataSet(posModule, shopId,
                            computerId, dsPrintData, dbServer, dbName, dbPort);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error($"Printer error => {ex.Message}");
            }
        }
    }
}