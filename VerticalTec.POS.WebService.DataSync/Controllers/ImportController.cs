﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Http;
using VerticalTec.POS.Database;
using VerticalTec.POS.Utils;
using VerticalTec.POS.WebService.DataSync.Models;
using vtecPOS_SQL.POSControl;

namespace VerticalTec.POS.WebService.DataSync.Controllers
{
    public class ImportController : ApiController
    {
        IDatabase _database;
        POSModule _posModule;

        public ImportController(IDatabase database, POSModule posModule)
        {
            _database = database;
            _posModule = posModule;
        }

        [HttpPost]
        [Route("v1/import/inv")]
        public async Task<IHttpActionResult> ImportInventoryDataAsync([FromBody]object data)
        {
            var result = new HttpActionResult<string>(Request);
            if (data == null)
            {
                var msg = $"Invalid json format!";
                await LogManager.Instance.WriteLogAsync(msg);
                result.StatusCode = HttpStatusCode.BadRequest;
                result.Message = msg;
                return result;
            }
            try
            {
                await LogManager.Instance.WriteLogAsync($"Incoming inventory import data {JsonConvert.SerializeObject(data, formatting: Formatting.None)}");
            }
            catch (Exception ex)
            {
                await LogManager.Instance.WriteLogAsync($"Invalid json format of inventory data {ex.Message}", LogManager.LogTypes.Error);
            }
            using (var conn = await _database.ConnectAsync())
            {
                var respText = "";
                var syncJson = "";
                var dataSet = new DataSet();
                var success = _posModule.ImportInventData(ref syncJson, ref respText, dataSet, data.ToString(), conn as SqlConnection);
                if (success)
                {
                    result.Success = success;
                    result.StatusCode = HttpStatusCode.Created;
                    result.Data = syncJson;

                    await LogManager.Instance.WriteLogAsync("Import inventory data successfully");
                }
                else
                {
                    result.StatusCode = HttpStatusCode.InternalServerError;
                    result.Message = respText;

                    await LogManager.Instance.WriteLogAsync($"Import inventory data {respText}", LogManager.LogTypes.Error);
                }
            }
            return result;
        }
    }
}
