﻿using CoinController;
using coinpanic_airdrop.Database;
using coinpanic_airdrop.Models;
using NBitcoin;
using RestSharp;
using shortid;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;
using static coinpanic_airdrop.Services.MailingService;

namespace coinpanic_airdrop.Controllers
{
    public class ClaimController : Controller
    {

        private CoinpanicContext db = new CoinpanicContext();

        // GET: NewClaim
        public async Task<ActionResult> NewClaim(string coin)
        {
            string claimId = ShortId.Generate(useNumbers: false, useSpecial: false, length: 10);
            string ip = Request.UserHostAddress;

            //ensure unique
            while (db.Claims.Where(c => c.ClaimId == claimId).Count() > 0)
                claimId = ShortId.Generate(useNumbers: false, useSpecial: false, length: 10);

            db.Claims.Add(new CoinClaim()
            {
                ClaimId = claimId,
                CoinShortName = coin,
                RequestIP = ip
            });
            var res = await db.SaveChangesAsync();

            if (Forks.ForkShortName.Values.Contains(coin))
            {
                var NewClaim = new CoinClaim { CoinShortName = coin, ClaimId = claimId };

                return View(NewClaim);
            }
            else
            {
                return RedirectToAction("InvalidCoin");
            }
            
        }

        [AllowAnonymous]
        [HttpPost]
        public ActionResult InitializeClaim(string claimId, string PublicKeys, string depositAddress, string emailAddress)
        {
            var userclaim = db.Claims.Where(c => c.ClaimId == claimId).Include(c => c.InputAddresses).First();

            userclaim.DepositAddress = depositAddress;
            userclaim.Email = emailAddress;

            List<string> list = new List<string>(
                           PublicKeys.Split(new string[] { "\r\n" },
                           StringSplitOptions.RemoveEmptyEntries));

            if (list.Count < 1)
                return RedirectToAction("ClaimError", new { message = "You must enter at least one address to claim", claimId = claimId });

            
            if (!Bitcoin.IsValidAddress(depositAddress))
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
                claimcoins = scanner.GetUnspentTransactionOutputs(claimAddresses, Forks.ForkShortNameCode[userclaim.CoinShortName]);
            }
            catch
            {
                return RedirectToAction("ClaimError", new { message = "Error searching for your addresses in the blockchain", claimId = claimId });
            }

            var amounts = scanner.CalculateOutputAmounts_Their_My_Fee(claimcoins.Item1, 0.05, 0.0001 * claimcoins.Item1.Count);
            var balances = claimcoins.Item2;

            List<InputAddress> inputs = list.Select(li => new InputAddress()
            {
                AddressId = Guid.NewGuid(),
                PublicAddress = li,
                CoinShortName = userclaim.CoinShortName,
                ClaimId = userclaim.ClaimId,
                ClaimValue = balances[li],
            }).ToList();

            userclaim.InputAddresses = inputs;

            userclaim.Deposited = Convert.ToDouble(amounts[0].ToDecimal(MoneyUnit.BTC));
            userclaim.MyFee = Convert.ToDouble(amounts[1].ToDecimal(MoneyUnit.BTC));
            userclaim.MinerFee = Convert.ToDouble(amounts[2].ToDecimal(MoneyUnit.BTC));

            userclaim.TotalValue = userclaim.Deposited + userclaim.MyFee + userclaim.MinerFee;

            // Generate unsigned tx
            var mydepaddr = System.Configuration.ConfigurationManager.AppSettings[userclaim.CoinShortName + "Deposit"];

            var utx = Bitcoin.GenerateUnsignedTX(claimcoins.Item1, amounts, Bitcoin.ParseAddress(userclaim.DepositAddress),
                Bitcoin.ParseAddress(mydepaddr),
                Forks.ForkShortNameCode[userclaim.CoinShortName]);

            userclaim.UnsignedTX = utx;

            // Generate witness data

            var w = Bitcoin.GetBlockData(claimcoins.Item1);
            userclaim.BlockData = w;

            db.SaveChanges();

            MonitoringService.SendMessage("New " + userclaim.CoinShortName + " claim", "new claim Initialized. https://www.coinpanic.com/Claim/ClaimConfirm?claimId=" + claimId + " " + " for " + userclaim.CoinShortName);

            return RedirectToAction("ClaimConfirm", new { claimId = claimId });
        }

        public ActionResult ClaimConfirm(string claimId)
        {
            try
            {
                var userclaim = db.Claims.Where(c => c.ClaimId == claimId).Include(c => c.InputAddresses).First();
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
            Response.Write(userclaim.First().BlockData);
            Response.End();

            // Not sure what else to do here
            return Content(String.Empty);
        }

        [AllowAnonymous]
        [HttpGet]
        public ActionResult CheckNode(string coin)
        {
            // List of seed nodes
            var userclaim = db.SeedNodes.Where(n => n.Coin == coin);


            var nodes = CoinPanicNodes.GetNodes(coin: coin);
            string nstatus = "";
            string connectedtimes = "";
            string nodestates = "";
            int numnodes = 0;
            foreach(var n in nodes)
            {
                if (n != null)
                {
                    if (n.IsConnected)
                    {
                        numnodes += 1;
                        nstatus += n.Peer.Endpoint.Address.ToString() + (n.IsConnected ? " is connected." : " is disconnected.  ");
                        connectedtimes += n.Peer.Endpoint.Address.ToString() + ":" + n.ConnectedAt.ToUniversalTime().ToString();
                        nodestates += n.Peer.Endpoint.Address.ToString() + ":" + n.State.ToString();
                    }
                }
            }
            ViewBag.result = nstatus;
            ViewBag.connectedTime = connectedtimes;
            ViewBag.nodeState = nodestates;
            ViewBag.numnodes = Convert.ToString(numnodes);
            return View();
        }

        [AllowAnonymous]
        [HttpPost]
        public ActionResult TransmitTransaction(string claimId, string signedTransaction)
        {
            var userclaim = db.Claims.Where(c => c.ClaimId == claimId).First();
            ViewBag.content = userclaim.CoinShortName + " not currently supported.";
            ViewBag.ClaimId = claimId;
            userclaim.SignedTX = signedTransaction.Trim(' ');
            Transaction t;
            try
            { 
                t = Transaction.Parse(signedTransaction.Trim(' '));
            }
            catch
            {
                MonitoringService.SendMessage("Invalid tx " + userclaim.CoinShortName + " submitted " + Convert.ToString(userclaim.TotalValue), "Claim broadcast: https://www.coinpanic.com/Claim/ClaimConfirm?claimId=" + claimId + " " + " for " + userclaim.CoinShortName);
                return RedirectToAction("ClaimError", new { message = "Unable to parse signed transaction: \r\n" + signedTransaction, claimId = claimId });
            }
            string txid = t.GetHash().ToString();
            userclaim.TransactionHash = txid;
            db.SaveChanges();
            
            if (userclaim.CoinShortName == "B2X")
            {
                userclaim.SignedTX = signedTransaction;
                var client = new RestClient("http://explorer.b2x-segwit.io/b2x-insight-api/");
                var request = new RestRequest("tx/send/", Method.POST);
                request.AddJsonBody(new { rawtx = signedTransaction });
                //request.AddParameter("rawtx", signedTransaction);

                IRestResponse response = client.Execute(request);
                var content = response.Content; // raw content as string
                ViewBag.content = content;
                userclaim.TransactionHash = content;
                userclaim.WasTransmitted = true;
                MonitoringService.SendMessage("New " + userclaim.CoinShortName + " broadcasting " + Convert.ToString(userclaim.TotalValue), "Claim broadcast: https://www.coinpanic.com/Claim/ClaimConfirm?claimId=" + claimId + " " + " for " + userclaim.CoinShortName + "\r\n txid: " + txid);

                db.SaveChanges();
            }
            else if (userclaim.CoinShortName == "BTG")
            {
                userclaim.SignedTX = signedTransaction;
                var client = new RestClient(" https://btgexplorer.com/api/");
                var request = new RestRequest("tx/send", Method.POST);
                request.AddJsonBody(new { rawtx = signedTransaction });

                IRestResponse response = client.Execute(request);
                var content = response.Content; // raw content as string
                ViewBag.content = content;
                userclaim.TransactionHash = content;
                userclaim.WasTransmitted = true;
                MonitoringService.SendMessage("New " + userclaim.CoinShortName + " broadcasting " + Convert.ToString(userclaim.TotalValue), "Claim broadcast: https://www.coinpanic.com/Claim/ClaimConfirm?claimId=" + claimId + " " + " for " + userclaim.CoinShortName + "\r\n txid: " + txid);

                db.SaveChanges();
            }
            else
            {
                //broadcast it directly to a node
                var txed = CoinPanicNodes.BroadcastTransaction(coin: userclaim.CoinShortName, transaction: t);
                if (txed > 0)
                {
                    //broadcasted
                    ViewBag.content = "Transaction was broadcast to " + Convert.ToString(txed) + "node(s).  Your transaction id is: " + txid;
                    userclaim.TransactionHash = txid;
                    userclaim.WasTransmitted = true;
                    userclaim.SignedTX = signedTransaction;
                    MonitoringService.SendMessage("New " + userclaim.CoinShortName + " broadcasting " + Convert.ToString(userclaim.TotalValue), "Claim broadcast: https://www.coinpanic.com/Claim/ClaimConfirm?claimId=" + claimId + " " + " for " + userclaim.CoinShortName + "\r\n txid: " + txid);

                }
                else
                {
                    ViewBag.content = "Error broadcasting your transaction.";
                    userclaim.WasTransmitted = false;
                    userclaim.SignedTX = signedTransaction;
                    MonitoringService.SendMessage("New " + userclaim.CoinShortName + " error broadcasting " + Convert.ToString(userclaim.TotalValue), "Claim broadcast: https://www.coinpanic.com/Claim/ClaimConfirm?claimId=" + claimId + " " + " for " + userclaim.CoinShortName + "\r\n txid: " + txid);

                }
                db.SaveChanges();
            }
            db.SaveChanges();
            //
            //else if (userclaim.CoinShortName == "BCX")  //Bitcoin Faith
            //{
            //    //https://www.coinpanic.com/Claim/ClaimConfirm?claimId=kIXqSkIskQ
            //    userclaim.SignedTX = signedTransaction;

            //    List<string> nodeips = new List<string>()
            //    {
            //        "192.169.153.174",
            //        "192.169.154.185",
            //        "120.131.13.249",
            //    };
            //    List<string> results = new List<string>();
            //    string result = "";
            //    bool success = false;
            //    foreach (string nip in nodeips)
            //    {
            //        var BitcoinNode = new BitcoinNode(address: nip, port: 9003);
            //        try
            //        {
            //            result = BitcoinNode.BroadcastTransaction(transaction, Forks.ForkShortNameCode[userclaim.CoinShortName]);

            //            if (result == transaction.GetHash().ToString() )
            //            {
            //                success = true;
            //                break;
            //            }
            //            else if(result.Substring(0, 6) == "Reject")
            //            {
            //                success = false;
            //                break;
            //            }
            //            else
            //            {
            //                results.Add(result);
            //            }
            //        }
            //        catch (Exception e)
            //        {
            //            results.Add(nip + ":" + e.Message);
            //        }
            //    }
            //    if (success)
            //    {
            //        ViewBag.content = "Coins successfully broadcast.  Your transaction is: " + transaction.GetHash().ToString();
            //        userclaim.TransactionHash = transaction.GetHash().ToString();
            //        userclaim.WasTransmitted = true;
            //        userclaim.SignedTX = signedTransaction;
            //    }
            //    else
            //    {
            //        ViewBag.content = "Error broadcasting your transaction: " + String.Join(";", results.ToArray());
            //        userclaim.WasTransmitted = false;
            //        userclaim.SignedTX = signedTransaction;
            //    }
            //    db.SaveChanges();
            //}
            //else if (userclaim.CoinShortName == "BTF")  //Bitcoin Faith
            //{
            //    //http://localhost:53483/Claim/ClaimConfirm?claimId=YdJGSDTzfN
            //    userclaim.SignedTX = signedTransaction;
            //    Transaction transaction = Transaction.Parse(signedTransaction);
            //    List<string> nodeips = new List<string>()
            //    {
            //        "47.90.38.149",
            //        "120.55.126.189",
            //        "47.90.16.179",
            //        "47.90.38.158",
            //        "47.90.37.123", //b.btf.hjy.cc
            //        "47.90.62.100",
            //    };
            //    //port 8346
            //    List<string> results = new List<string>();
            //    string result = "";
            //    bool success = false;
            //    foreach (string nip in nodeips)
            //    {
            //        var BitcoinNode = new BitcoinNode(address: nip, port: 8346);
            //        try
            //        {
            //            result = BitcoinNode.BroadcastTransaction(transaction, Forks.ForkShortNameCode[userclaim.CoinShortName]);

            //            if (result == transaction.GetHash().ToString())
            //            {
            //                success = true;
            //                break;
            //            }
            //            else
            //            {
            //                results.Add(result);
            //            }
            //        }
            //        catch (Exception e)
            //        {
            //            results.Add(nip + ":" + e.Message);
            //        }
            //    }
            //    if (success)
            //    {
            //        ViewBag.content = "Coins successfully broadcast.  Your transaction is: " + transaction.GetHash().ToString();
            //        userclaim.TransactionHash = transaction.GetHash().ToString();
            //        userclaim.WasTransmitted = true;
            //        userclaim.SignedTX = signedTransaction;
            //    }
            //    else
            //    {
            //        ViewBag.content = "Error broadcasting your transaction: " + String.Join(";", results.ToArray());
            //        userclaim.WasTransmitted = false;
            //        userclaim.SignedTX = signedTransaction;
            //    }
            //    db.SaveChanges();
            //}
            //else if (userclaim.CoinShortName == "SBTC") //Super bitcoin
            //{
            //    userclaim.SignedTX = signedTransaction;
            //    Transaction transaction = Transaction.Parse(signedTransaction);

            //    List<string> nodeips = new List<string>()
            //    {
            //        "185.17.31.58",
            //        "162.212.157.232",
            //        "101.201.117.68",
            //        "162.212.157.232",
            //        "123.56.143.216"
            //    };
            //    List<string> results = new List<string>();
            //    string result = "";
            //    bool success = false;
            //    foreach (string nip in nodeips)
            //    {
            //        try
            //        {


            //            var BitcoinNode = new BitcoinNode(address: nip, port: 8334);
            //            result = BitcoinNode.BroadcastTransaction(transaction, Forks.ForkShortNameCode[userclaim.CoinShortName]);

            //            if (result == transaction.GetHash().ToString())
            //            {
            //                success = true;
            //                break;
            //            }
            //            else
            //            {
            //                results.Add(result);
            //            }
            //        }
            //        catch (Exception e)
            //        {
            //            results.Add(nip + ":" + e.Message);
            //        }
            //    }
            //    if (success)
            //    {
            //        ViewBag.content = "Coins successfully broadcast.  Your transaction is: " + transaction.GetHash().ToString();
            //        userclaim.TransactionHash = transaction.GetHash().ToString();
            //        userclaim.WasTransmitted = true;
            //        userclaim.SignedTX = signedTransaction;
            //    }
            //    else
            //    {
            //        ViewBag.content = "Error broadcasting your transaction: " + String.Join(";", results.ToArray());
            //        userclaim.WasTransmitted = false;
            //        userclaim.SignedTX = signedTransaction;
            //    }
            //    db.SaveChanges();
            //}

            return View();
        }
    }
}

/*
.controller(
 "SendRawTransactionController",
 function($scope,$http,Api)
 {
    $scope.transaction="",
    $scope.status="ready",
    $scope.txid="",
    $scope.error=null,
    $scope.formValid=function()
    {
        return!!$scope.transaction
    },
    $scope.send=function()
    {
        var postData={rawtx:$scope.transaction};
        $scope.status="loading",
        $http.post(Api.apiPrefix+"/tx/send",postData)
            .success(
                function(data,status,headers,config)
                {
                    return"string"!=typeof data.txid?($scope.status="error",void($scope.error="The transaction was sent but no transaction id was got back")):($scope.status="sent",void($scope.txid=data.txid))
                })
            .error(
                function(data,status,headers,config)
                {
                    $scope.status="error",
                    data?$scope.error=data:$scope.error="No error message given (connection error?)"
                })
    }
 })

*/
