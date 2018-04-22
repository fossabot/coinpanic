﻿using CoinController;
using coinpanic_airdrop.Database;
using CoinpanicLib.Models;
using NBitcoin;
using NBitcoin.Forks;
using RestSharp;
using shortid;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;
using static CoinpanicLib.Services.MailingService;

namespace coinpanic_airdrop.Controllers
{
    public class ClaimController : Controller
    {
        private CoinpanicContext db = new CoinpanicContext();

        // GET: NewClaim
        public async Task<ActionResult> NewClaim(string coin, string coupon)
        {
            string claimId = ShortId.Generate(useNumbers: false, useSpecial: false, length: 10);
            string ip = Request.UserHostAddress;

            // Ensure the ClaimId is unique
            while (db.Claims.Where(c => c.ClaimId == claimId).Count() > 0)
                claimId = ShortId.Generate(useNumbers: false, useSpecial: false, length: 10);

            db.Claims.Add(new CoinClaim()
            {
                ClaimId = claimId,
                CoinShortName = coin,
                RequestIP = ip
            });
            var res = await db.SaveChangesAsync();

            // Make sure we understand how to sign the requested coin
            if (BitcoinForks.ForkByShortName.Keys.Contains(coin))
            {
                var NewClaim = new CoinClaim { CoinShortName = coin, ClaimId = claimId };
                return View(NewClaim);
            }
            else
            {
                return RedirectToAction("InvalidCoin");
            }
        }

        [HttpPost, AllowAnonymous]
        public ActionResult InitializeClaim(string claimId, string PublicKeys, string depositAddress, string emailAddress)
        {
            var userclaim = db.Claims.Where(c => c.ClaimId == claimId).Include(c => c.InputAddresses).First();

            //clean up
            depositAddress = depositAddress.Replace("\n", String.Empty);
            depositAddress = depositAddress.Replace("\r", String.Empty);
            depositAddress = depositAddress.Replace("\t", String.Empty);
            depositAddress = depositAddress.Trim().Replace(" ", "");

            userclaim.DepositAddress = depositAddress;
            userclaim.Email = emailAddress;

            List<string> list = new List<string>(
                           PublicKeys.Split(new string[] { "\r\n" },
                           StringSplitOptions.RemoveEmptyEntries));

            if (list.Count < 1)
                return RedirectToAction("ClaimError", new { message = "You must enter at least one address to claim", claimId = claimId });
            
            if (!Bitcoin.IsValidAddress(depositAddress, userclaim.CoinShortName))
                return RedirectToAction("ClaimError", new { message = "Deposit Address not valid", claimId = claimId });

            var invalid = list.Where(a => !Bitcoin.IsValidAddress(a));
            if (invalid.Count() > 0)
            {
                return RedirectToAction("ClaimError", new { message = String.Join(", ",invalid) + (invalid.Count() < 2 ? " is" : " are") + " invalid.", claimId = claimId });
            }

            var scanner = new BlockScanner();
            var claimAddresses = Bitcoin.ParseAddresses(list);

            Tuple<List<ICoin>, Dictionary<string, double>> claimcoins;
            try
            { 
                claimcoins = scanner.GetUnspentTransactionOutputs(claimAddresses, userclaim.CoinShortName);
            }
            catch (Exception e)
            {
                return RedirectToAction("ClaimError", new { message = "Error searching for your addresses in the blockchain", claimId = claimId });
            }

            var amounts = scanner.CalculateOutputAmounts_Their_My_Fee(claimcoins.Item1, 0.05, 0.0003 * claimcoins.Item1.Count);
            var balances = claimcoins.Item2;

            List<InputAddress> inputs;
            if (userclaim.CoinShortName == "BTCP")
            {
                inputs = list.Select(li => new InputAddress()
                {
                    AddressId = Guid.NewGuid(),
                    PublicAddress = li + " -> " + Bitcoin.ParseAddress(li).Convert(Network.BTCP).ToString(),
                    CoinShortName = userclaim.CoinShortName,
                    ClaimId = userclaim.ClaimId,
                    ClaimValue = balances[li],
                }).ToList();
            }
            else
            {
                inputs = list.Select(li => new InputAddress()
                    {
                        AddressId = Guid.NewGuid(),
                        PublicAddress = li,
                        CoinShortName = userclaim.CoinShortName,
                        ClaimId = userclaim.ClaimId,
                        ClaimValue = balances[li],
                    }).ToList();
            }
            

            userclaim.InputAddresses = inputs;
            userclaim.Deposited = Convert.ToDouble(amounts[0].ToDecimal(MoneyUnit.BTC));
            userclaim.MyFee = Convert.ToDouble(amounts[1].ToDecimal(MoneyUnit.BTC));
            userclaim.MinerFee = Convert.ToDouble(amounts[2].ToDecimal(MoneyUnit.BTC));
            userclaim.TotalValue = userclaim.Deposited + userclaim.MyFee + userclaim.MinerFee;
            userclaim.InitializeDate = DateTime.Now;

            if (userclaim.Deposited < 0)
                userclaim.Deposited = 0;
            if (userclaim.MyFee < 0)
                userclaim.MyFee = 0;

            // Generate unsigned tx
            var mydepaddr = ConfigurationManager.AppSettings[userclaim.CoinShortName + "Deposit"];

            var utx = Bitcoin.GenerateUnsignedTX(claimcoins.Item1, amounts, Bitcoin.ParseAddress(userclaim.DepositAddress, userclaim.CoinShortName),
                Bitcoin.ParseAddress(mydepaddr, userclaim.CoinShortName),
                userclaim.CoinShortName);

            userclaim.UnsignedTX = utx;

            // Generate witness data
            var w = Bitcoin.GetBlockData(claimcoins.Item1);
            userclaim.BlockData = w;

            // New format of message
            BlockData bd = new BlockData()
            {
                fork = userclaim.CoinShortName,
                coins = claimcoins.Item1,
                utx = utx,
                addresses = balances.Select(kvp => kvp.Key).ToList(),
            };
            string bdstr = NBitcoin.JsonConverters.Serializer.ToString(bd);
            userclaim.ClaimData = bdstr;

            db.SaveChanges();

            MonitoringService.SendMessage("New " + userclaim.CoinShortName + " claim", "new claim Initialized. https://www.coinpanic.com/Claim/ClaimConfirm?claimId=" + claimId + " " + " for " + userclaim.CoinShortName);

            return RedirectToAction("ClaimConfirm", new { claimId = claimId });
        }

        /// <summary>
        /// Controller for the claim confirmation page, where users will
        /// review the claim and get instructions for signing.
        /// </summary>
        /// <param name="claimId"></param>
        /// <returns></returns>
        public ActionResult ClaimConfirm(string claimId)
        {
            try
            {
                var userclaim = db.Claims.Where(c => c.ClaimId == claimId).Include(c => c.InputAddresses).First();
                ViewBag.Multiplier = BitcoinForks.ForkByShortName[userclaim.CoinShortName].Multiplier;
                return View(userclaim);
            }
            catch
            {
                return RedirectToAction("ClaimError", new { message = "claimId not valid", claimId = claimId });
            }
        }

        public ActionResult ClaimError(string message, string claimId)
        {
            ViewBag.Title = "Claim Error";
            ViewBag.message = message;
            ViewBag.ClaimId = claimId;
            return View();
        }

        public ActionResult InvalidCoin()
        {
            return View();
        }

        public ActionResult DownloadTransactionFile(string claimId)
        {
            var userclaim = db.Claims.Where(c => c.ClaimId == claimId);

            if (userclaim.Count() < 1)
            {
                return RedirectToAction("ClaimError", new { message = "Unable to find data for claim " + claimId });
            }

            Response.Clear();
            Response.AddHeader("Content-Disposition", "attachment; filename=BlockChainData.txt");
            Response.ContentType = "text/json";

            // Write all my data
            string blockdata = userclaim.First().BlockData;
            Response.Write(blockdata);
            Response.End();

            // Not sure what else to do here
            return Content(String.Empty);
        }

        public ActionResult DownloadClaimDataFile(string claimId)
        {
            var userclaim = db.Claims.Where(c => c.ClaimId == claimId);

            if (userclaim.Count() < 1)
            {
                return RedirectToAction("ClaimError", new { message = "Unable to find data for claim " + claimId });
            }

            Response.Clear();
            Response.AddHeader("Content-Disposition", "attachment; filename=ClaimData.txt");
            Response.ContentType = "text/json";

            // Write all my data
            string blockdata = userclaim.First().ClaimData;
            Response.Write(blockdata);
            Response.End();

            // Not sure what else to do here
            return Content(String.Empty);
        }
    }
}