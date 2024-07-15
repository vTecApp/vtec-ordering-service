﻿using DevExpress.XtraExport.Helpers;
using MySql.Data.MySqlClient;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading.Tasks;
using System.Web.Http;
using VerticalTec.POS.Database;
using VerticalTec.POS.Service.Ordering.Owin.Services;
using vtecPOS.GlobalFunctions;
using vtecPOS.POSControl;

namespace VerticalTec.POS.Service.Ordering.Owin.Controllers
{
    [RoutePrefix("inventory")]

    public class InventoryController : ApiController
    {
        private IDatabase _database;
        private VtecPOSRepo _vtecRepo;
        private InventModule _inventModule;

        public InventoryController(IDatabase database, VtecPOSRepo vtecRepo)
        {
            _database = database;
            _vtecRepo = vtecRepo;

            _inventModule = new InventModule();
        }

        [HttpPost]
        [Route("login")]
        public async Task<IHttpActionResult> LoginAsync(object data)
        {
            try
            {
                var jObj = JObject.Parse(data.ToString());
                using (var conn = (MySqlConnection)await _database.ConnectAsync())
                {
                    var cmd = new MySqlCommand("select StaffID, StaffRoleID, StaffFirstName, StaffLastName from staffs where Deleted=0 and StaffLogin=@username and StaffPassword=UPPER(SHA1(@password))", conn);
                    cmd.Parameters.Add(new MySqlParameter("@username", jObj["username"]));
                    cmd.Parameters.Add(new MySqlParameter("@password", jObj["password"]));

                    var dt = new DataTable();
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        dt.Load(reader);
                    }

                    if (dt.Rows.Count > 0)
                    {
                        return Ok(new
                        {
                            Status = HttpStatusCode.OK,
                            StatusCode = "200.200",
                            Data = dt.AsEnumerable().Select(r => new
                            {
                                StaffID = r["StaffID"],
                                StaffRoleID = r["StaffRoleID"],
                                StaffFirstName = r["StaffFirstName"],
                                SstaffLastName = r["StaffLastName"]
                            }).FirstOrDefault()
                        });
                    }
                    else
                    {
                        return Ok(new
                        {
                            Status = HttpStatusCode.NotFound,
                            StatusCode = "404.404",
                            Message = "Not found staff information"
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                return Ok(new
                {
                    Status = HttpStatusCode.InternalServerError,
                    StatusCode = "500.500",
                    Message = ex.Message
                });
            }
        }

        [HttpGet]
        [Route("shops")]
        public async Task<IHttpActionResult> GetShopAsync()
        {
            try
            {
                using (var conn = (MySqlConnection)await _database.ConnectAsync())
                {
                    var cmd = new MySqlCommand("select ShopID,ShopCode,ShopName from shop_data where Deleted=0 and IsInv=1", conn);
                    var dt = new DataTable();
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        dt.Load(reader);
                    }
                    return Ok(new
                    {
                        Status = HttpStatusCode.OK,
                        StatusCode = "200.200",
                        Data = dt
                    });
                }
            }
            catch (Exception ex)
            {
                return Ok(new
                {
                    Status = HttpStatusCode.InternalServerError,
                    StatusCode = "500.500",
                    Message = ex.Message
                });
            }
        }

        [HttpPost]
        [Route("hht/createdocument")]
        public async Task<IHttpActionResult> CreateDocument(InventObject.DocumentData documentData, int staffId = 2, int langId = 1)
        {
            try
            {
                using (var conn = (MySqlConnection)await _database.ConnectAsync())
                {
                    var docParam = new InventObject.DocParam()
                    {
                        StaffID = staffId,
                        DocumentDate = documentData.DocHeader.DocumentDate,
                        DocumentTypeID = documentData.DocHeader.DocumentTypeID,
                        LangID = langId
                    };

                    var docData = await Task.Run(() =>
                    {
                        var respText = "";
                        var resultDocData = new InventObject.DocumentData();
                        using (var _ = new InvariantCultureScope())
                        {
                            if (_inventModule.DocumentProcess(ref respText, ref resultDocData, documentData, docParam, conn) == false)
                                throw new Exception($"DocumentProcess: {respText}");

                            resultDocData.DocDetail = documentData.DocDetail;

                            if (resultDocData.DocDetail?.Any() == true)
                            {
                                for (var i = 0; i < resultDocData.DocDetail.Count; i++)
                                {
                                    var d = resultDocData.DocDetail[i];
                                    var docDetail = new InventObject.DocDetail();
                                    if (_inventModule.DocDetailObj(ref respText, ref docDetail, d.DocDetailID, resultDocData.DocHeader.DocumentKey, d.MaterialID, "front", conn) == false)
                                        throw new Exception($"DocDetailObj: {respText}");

                                    docDetail.TotalQty = d.TotalQty;
                                    docDetail.PricePerUnit = d.PricePerUnit;

                                    if (string.IsNullOrEmpty(docDetail.CurrentQty))
                                        docDetail.CurrentQty = "0";
                                    if (string.IsNullOrEmpty(docDetail.DiffQty))
                                        docDetail.DiffQty = "0";
                                    if (string.IsNullOrEmpty(docDetail.DiscBill))
                                        docDetail.DiscBill = "0";
                                    if (string.IsNullOrEmpty(docDetail.DiscountValue))
                                        docDetail.DiscountValue = "0";
                                    if (string.IsNullOrEmpty(docDetail.ItemDiscountAmt))
                                        docDetail.ItemDiscountAmt = "0";
                                    if (string.IsNullOrEmpty(docDetail.GrandTotal))
                                        docDetail.GrandTotal = "0";
                                    if (string.IsNullOrEmpty(docDetail.ReqQty))
                                        docDetail.ReqQty = "0";
                                    if (string.IsNullOrEmpty(docDetail.SubTotal))
                                        docDetail.SubTotal = "0";
                                    if (string.IsNullOrEmpty(docDetail.TRQty))
                                        docDetail.TRQty = "0";
                                    if (string.IsNullOrEmpty(docDetail.TotalExtVAT))
                                        docDetail.TotalExtVAT = "0";
                                    if (string.IsNullOrEmpty(docDetail.TotalIncVAT))
                                        docDetail.TotalIncVAT = "0";
                                    if (string.IsNullOrEmpty(docDetail.TotalVAT))
                                        docDetail.TotalVAT = "0";

                                    resultDocData.DocDetail[i] = docDetail;
                                }

                                var isAddDocDetailSucc = _inventModule.DocDetail_Add(ref respText, ref resultDocData, resultDocData.DocDetail, resultDocData.DocHeader, conn);
                                if (!isAddDocDetailSucc || !string.IsNullOrEmpty(respText))
                                    throw new Exception($"DocDetail_Add: {respText}");
                            }
                        }
                        return resultDocData;
                    });

                    return Ok(new
                    {
                        Status = HttpStatusCode.OK,
                        StatusCode = "200.201",
                        Data = docData
                    });
                }
            }
            catch (Exception ex)
            {
                return Ok(new
                {
                    Status = HttpStatusCode.InternalServerError,
                    StatusCode = "500.500",
                    Message = ex.Message
                });
            }
        }

        [HttpGet]
        [Route("masterdata")]
        public async Task<IHttpActionResult> GetMasterDataAsync(int documentType, int staffId, int langId = 1)
        {
            try
            {
                using (var conn = (MySqlConnection)await _database.ConnectAsync())
                {
                    var docParam = new InventObject.DocParam()
                    {
                        DocumentDate = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        StaffID = staffId,
                        DocumentTypeID = documentType,
                        LangID = langId
                    };

                    var documentObj = new InventObject.DocumentObj();
                    var respText = "";

                    if (_inventModule.DocumentObj(ref respText, ref documentObj, docParam, conn) == false)
                        throw new Exception(respText);

                    var cmdText = @"select * from materialdocumenttypeunitsetting;
                        select a.MaterialID,a.MaterialCode,a.MaterialBarcode,a.MaterialName,c.UnitSmallID,c.UnitLargeID,c.MaterialUnitRatioCode,d.UnitLargeName 
                        from materials a 
                        inner join unitsmall b on a.UnitSmallID=b.UnitSmallID 
                        inner join unitratio c on b.UnitSmallID=c.UnitSmallID 
                        inner join unitlarge d on c.UnitLargeID=d.UnitLargeID
                        where a.Deleted=0 and c.Deleted=0;";
                    var cmd = new MySqlCommand(cmdText, conn);
                    cmd.Parameters.Add(new MySqlParameter("@docType", documentType));
                    var adapter = new MySqlDataAdapter(cmd);
                    var ds = new DataSet();
                    adapter.Fill(ds);
                    ds.Tables[0].TableName = "UnitSettings";
                    ds.Tables[1].TableName = "Materials";

                    return Ok(new
                    {
                        Status = HttpStatusCode.OK,
                        StatusCode = "200.200",
                        Data = new
                        {
                            MaterialData = ds,
                            Vendors = documentObj.Vendors
                        }
                    });
                }
            }
            catch (Exception ex)
            {

                return Ok(new
                {
                    Status = HttpStatusCode.InternalServerError,
                    StatusCode = "500.500",
                    Message = ex.Message
                });
            }
        }
    }
}
