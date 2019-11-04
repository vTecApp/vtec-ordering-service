﻿using System.Data;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Web.Http;
using VerticalTec.POS.Database;
using VerticalTec.POS.Utils;
using VerticalTec.POS.Service.Ordering.Owin.Models;

namespace VerticalTec.POS.OrderingApi.Controllers
{
    public class StaffController : ApiController
    {
        IDatabase _database;
        VtecPOSRepo _posRepo;

        public StaffController(IDatabase database)
        {
            _database = database;
            _posRepo = new VtecPOSRepo(database);
        }
        //TODO: not hash password
        [HttpPost]
        [Route("v1/staffs")]
        public async Task<IHttpActionResult> IdentifyStaff(string staffCode = "", string password = "")
        {
            var result = new HttpActionResult<object>(Request);
            using (IDbConnection conn = await _database.ConnectAsync())
            {
                var dtStaff = await _posRepo.GetStaffAsync(conn, staffCode, password);
                var dtStaffPermission = await _posRepo.GetStaffPermissionAsync(conn);

                if (dtStaff.Rows.Count > 0)
                {
                    var staff = (from row in dtStaff.AsEnumerable()
                                 select new
                                 {
                                     StaffID = row.GetValue<int>("StaffID"),
                                     StaffRoleID = row.GetValue<int>("StaffRoleID"),
                                     StaffFirstName = row.GetValue<string>("StaffFirstName"),
                                     StaffLastName = row.GetValue<string>("StaffLastName"),
                                     LangID = row.GetValue<int>("LangID"),
                                     Permissions = (from permission in dtStaffPermission.Select($"StaffRoleID={row.GetValue<int>("StaffRoleID")}")
                                                    select new
                                                    {
                                                        PermissionItemID = permission.GetValue<int>("PermissionItemID")
                                                    }).ToList()
                                 }).FirstOrDefault();
                    result.StatusCode = HttpStatusCode.OK;
                    result.Body = staff;
                }
                else
                {
                    result.StatusCode = HttpStatusCode.NotFound;
                }
            }
            return result;
        }
    }
}
