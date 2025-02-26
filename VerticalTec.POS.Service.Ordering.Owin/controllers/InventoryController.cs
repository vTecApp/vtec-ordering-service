using DevExpress.XtraExport.Helpers;
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
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Results;
using VerticalTec.POS.Database;
using VerticalTec.POS.Service.Ordering.Owin.Services;
using vtecPOS.GlobalFunctions;
using vtecPOS.POSControl;

namespace VerticalTec.POS.Service.Ordering.Owin.Controllers
{
    [RoutePrefix("inventory")]

    public class InventoryController : ApiController
    {
        static readonly NLog.Logger _log = NLog.LogManager.GetLogger("logglobal");

        private IDatabase _database;
        private VtecPOSRepo _vtecRepo;
        private InventModule _inventModule;

        public InventoryController(IDatabase database, VtecPOSRepo vtecRepo)
        {
            _database = database;
            _vtecRepo = vtecRepo;

            _inventModule = new InventModule();
        }

        [HttpGet]
        [Route("barcode_product_name")]
        public async Task<IHttpActionResult> GetBarcodeProductName(string code)
        {
            if (string.IsNullOrEmpty(code))
                return BadRequest();

            var cmdText = "select p.ProductID,P.ProductCode,p.ProductName,p.ProductName1,p.ProductName2,p.ProductName3\r\nfrom products p\r\njoin products_barcode pb\r\non p.ProductID=pb.ProductID\r\nwhere pb.ProductBarCode=@code;";

            using (var conn = (MySqlConnection)await _database.ConnectAsync())
            {
                var cmd = new MySqlCommand(cmdText, conn);
                cmd.Parameters.Add(new MySqlParameter("@code", code.Trim()));

                var dt = new DataTable();
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    dt.Load(reader);
                }

                if (dt.Rows.Count > 0)
                {
                    var data = dt.AsEnumerable().Select(r => new
                    {
                        ProductID = r["ProductID"],
                        ProductName = r["ProductName"],
                        ProductName1 = r["ProductName1"],
                        ProductName2 = r["ProductName2"],
                        ProductName3 = r["ProductName3"],
                    }).First();
                    return Ok(data);
                }
                else
                {
                    return new NotFoundResult(Request);
                }
            }
        }

        [HttpDelete]
        [Route("product_label")]
        public async Task<IHttpActionResult> DeleteAllProductLabel()
        {
            using (var conn = (MySqlConnection)await _database.ConnectAsync())
            {
                var cmd = new MySqlCommand("truncate table product_label", conn);
                await cmd.ExecuteNonQueryAsync();
                return Ok();
            }
        }

        [HttpGet]
        [Route("product_label")]
        public async Task<IHttpActionResult> GetProductLabel()
        {
            using (var conn = (MySqlConnection)await _database.ConnectAsync())
            {
                var cmd = new MySqlCommand("select * from product_label", conn);
                var dt = new DataTable();
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    dt.Load(reader);
                }
                return Ok(dt);
            }
        }

        [HttpPost]
        [Route("v2/product_label")]
        public async Task<IHttpActionResult> AddProductLabelV2(List<object> datas)
        {
            using (var conn = (MySqlConnection)await _database.ConnectAsync())
            {
                var trn = (MySqlTransaction)null;
                try
                {
                    var strBuilder = new StringBuilder();
                    if (datas?.Any() == true)
                    {
                        strBuilder.Append("insert into product_label values ");

                        for (var i = 0; i < datas.Count(); i++)
                        {
                            var data = JObject.Parse(datas[i].ToString());
                            var code = data["ProductCode"];
                            var name = data["ProductName"];

                            strBuilder.Append($"('{code}', '{name}')");
                            if (i < datas.Count() - 1)
                                strBuilder.Append(",");
                        }
                    }
                    trn = conn.BeginTransaction();
                    var cmd = new MySqlCommand("truncate table product_label", conn);
                    await cmd.ExecuteNonQueryAsync();
                    if (strBuilder.Length > 0)
                    {
                        cmd.CommandText = strBuilder.ToString();
                        await cmd.ExecuteNonQueryAsync();
                    }
                    trn.Commit();
                    return Ok();
                }
                catch (Exception ex)
                {
                    trn?.Rollback();
                    return Content(HttpStatusCode.InternalServerError, ex.Message);
                }
            }
        }

        [HttpPost]
        [Route("product_label")]
        public async Task<IHttpActionResult> AddProductLabel(object[] data)
        {
            using (var conn = (MySqlConnection)await _database.ConnectAsync())
            {
                var trn = (MySqlTransaction)null;
                try
                {
                    var strBuilder = new StringBuilder();
                    if (data.Length > 0)
                    {
                        strBuilder.Append("insert into product_label values ");

                        for (var i = 0; i < data.Length; i++)
                        {
                            var code = data[i];
                            strBuilder.Append($"('{code}')");
                            if (i < data.Length - 1)
                                strBuilder.Append(",");
                        }
                    }
                    trn = conn.BeginTransaction();
                    var cmd = new MySqlCommand("truncate table product_label", conn);
                    await cmd.ExecuteNonQueryAsync();
                    if (strBuilder.Length > 0)
                    {
                        cmd.CommandText = strBuilder.ToString();
                        await cmd.ExecuteNonQueryAsync();
                    }
                    trn.Commit();
                    return Ok();
                }
                catch (Exception ex)
                {
                    trn?.Rollback();
                    return Content(HttpStatusCode.InternalServerError, ex.Message);
                }
            }
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
                _log.Error(ex, "Inventory Login");

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
                _log.Error(ex, "Inventory GetShop");

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
                                    docDetail.DiscountType = d.DiscountType;
                                    docDetail.DiscountValue = d.DiscountValue;

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
                _log.Error(ex, "Inventory CreateDocument");

                return Ok(new
                {
                    Status = HttpStatusCode.InternalServerError,
                    StatusCode = "500.500",
                    Message = ex.Message
                });
            }
        }

        [HttpGet]
        [Route("productinfo")]
        public async Task<IHttpActionResult> GetProductInfoAsync(string barcode, int shopId)
        {
            try
            {
                using (var conn = (MySqlConnection)await _database.ConnectAsync())
                {
                    var dfSaleModeId = Convert.ToInt32(await MySqlHelper.ExecuteScalarAsync(conn, "select SaleModeID from salemode where IsDefault=1;"));
                    var saleDate = DateTime.Today;
                    var cmdText = @"select a.MaterialID, a.MaterialCode, a.MaterialBarCode, a.MaterialName, c.UnitSmallID,c.UnitLargeID,c.UnitLargeRatio,c.UnitSmallRatio,d.UnitLargeName As UnitName ,c.MaterialUnitRatioCode
                                from materials a inner join unitsmall b on a.UnitSmallID=b.UnitSmallID
                                inner join unitratio c on b.UnitSmallID=c.UnitSmallID
                                inner join unitlarge d on c.UnitLargeID=d.UnitLargeID
                                where a.Deleted=0 and c.Deleted=0 and (a.MaterialCode=@barcode or a.MaterialBarCode=@barcode);
                                select p.ProductID, p.ProductCode, p.ProductName, case when mp.ProductPrice is null then md.ProductPrice else mp.ProductPrice end as ProductPrice 
                                from products p 
                                join materials m on p.ProductID=m.MaterialID
                                left outer join (select ProductID, ProductPrice from productprice where FromDate <= @saleDate and ToDate >= @saleDate and SaleMode=@dfSaleMode) mp on p.ProductID=mp.ProductID 
                                left outer join (select ProductID, ProductPrice from productprice where FromDate <= @saleDate and ToDate >= @saleDate and SaleMode=1) md on p.ProductID=md.ProductID 
                                where m.MaterialCode=@barcode and p.Deleted=0;";
                    var cmd = new MySqlCommand(cmdText, conn);
                    cmd.Parameters.Add(new MySqlParameter("@dfSaleMode", dfSaleModeId));
                    cmd.Parameters.Add(new MySqlParameter("@saleDate", saleDate));
                    cmd.Parameters.Add(new MySqlParameter("@barcode", barcode));

                    var ds = new DataSet();
                    var adapter = new MySqlDataAdapter(cmd);
                    adapter.Fill(ds);
                    ds.Tables[0].TableName = "MaterialData";
                    ds.Tables[1].TableName = "PriceData";

                    try
                    {
                        var materialId = ds.Tables[0].AsEnumerable().Select(r => (int)r["MaterialID"]).FirstOrDefault();
                        var posModule = new POSModule();
                        var respText = "";
                        var fromDate = $"'{DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}'";
                        var toDate = $"'{DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}'";
                        var docMonth = DateTime.Now.Month;
                        var docYear = DateTime.Now.Year;
                        var dtColumn = new DataTable();
                        var dtStock = new DataTable();

                        var isSucc = posModule.Report_StockCard(ref respText, ref dtColumn, ref dtStock, shopId, fromDate, toDate, docMonth, docYear, "", 0, 0, materialId, "", conn);
                        if (isSucc)
                        {
                            dtStock.TableName = "StockData";
                            ds.Tables.Add(dtStock);
                        }
                    }
                    catch { }

                    return Ok(new
                    {
                        Status = HttpStatusCode.OK,
                        StatusCode = "200.200",
                        Data = ds
                    });
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Inventory GetProductInfo");

                return Ok(new
                {
                    Status = HttpStatusCode.InternalServerError,
                    StatusCode = "500.500",
                    Message = ex.Message
                });
            }
        }

        [HttpGet]
        [Route("promotion")]
        public async Task<IHttpActionResult> GetPromotionInfoAsync(int productId)
        {
            try
            {
                using (var conn = (MySqlConnection)await _database.ConnectAsync())
                {
                    var cmd = new MySqlCommand();
                    cmd.Connection = conn;
                    cmd.CommandText = @"select a.*, b.TypeID, b.DiscountAmount as ItemDiscAmount, b.DiscountPercent as ItemDiscPercent 
                        from promotion a join promotionproducts b 
                        on a.PromotionID=b.PromotionID where a.Deleted=0 and a.Activated=1 and b.ProductID=@productId";
                    cmd.Parameters.Add(new MySqlParameter("@productId", productId));
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
                _log.Error(ex, "Inventory GetPromotion");

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

                    var cmdText = @"select a.*, b.UnitLargeName from materialdocumenttypeunitsetting a left join unitlarge b on a.UnitLargeID=b.UnitLargeID;
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
                _log.Error(ex, "Inventory GetMasterData");

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
