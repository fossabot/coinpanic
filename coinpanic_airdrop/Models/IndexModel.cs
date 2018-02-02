﻿using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Web;

namespace coinpanic_airdrop.Models
{
    
    public class IndexCoinInfo
    {
        [Key]
        public int InfoId { get; set; }
        public string Coin { get; set; }
        public string Status { get; set; } // online, degraded, offline
        public int Nodes { get; set; }
        public string CoinName { get; set; }
        public string CoinHeaderMessage { get; set; }
        public string CoinNotice { get; set; }
        public string AlertClass { get; set; }
        public string Exchange { get; set; }
        public string ExchangeURL { get; set; }
        public string ExchangeConfirm { get; set; }
    }

    public class IndexModel
    {
        public Dictionary<string, IndexCoinInfo> CoinInfo { get; set; }
    }
}