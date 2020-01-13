﻿using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace VerticalTec.POS.LiveUpdate
{
    public class VersionLiveUpdateLog
    {
        public string LogUUID { get; set; } = Guid.NewGuid().ToString().ToUpper();
        public DateTime SaleDate { get; set; } = DateTime.Today;
        public int ShopId { get; set; }
        public int ComputerId { get; set; }
        public ProgramTypes ProgramId { get; set; } = ProgramTypes.FrontCashier;
        public int ActionId { get; set; }
        public string ProgramVersion { get; set; }
        public int ActionStatus { get; set; }
        public DateTime StartTime { get; set; } = DateTime.MinValue;
        public DateTime EndTime { get; set; } = DateTime.MinValue;
        [MaxLength(2000)]
        public string LogMessage { get; set; }
    }
}
