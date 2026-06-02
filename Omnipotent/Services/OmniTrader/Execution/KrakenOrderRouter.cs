using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Persistence;
using System.Security.Cryptography;
using System.Text;

namespace Omnipotent.Services.OmniTrader.Execution
{
    /// <summary>
    /// Kraken REST order router. Signs requests with HMAC-SHA512 per Kraken's docs.
    /// </summary>
    public sealed class KrakenOrderRouter : IOrderRouter
    {
        private const string ApiBase = "https://api.kraken.com";
        private readonly HttpClient httpClient = new();
        private readonly string apiKey;
        private readonly byte[] apiSecret;
        private readonly KrakenNonceStore nonceStore;

        public KrakenOrderRouter(string apiKey, string apiSecretBase64, KrakenNonceStore nonceStore)
        {
            this.apiKey = apiKey;
            this.apiSecret = Convert.FromBase64String(apiSecretBase64);
            this.nonceStore = nonceStore;
        }

        public async Task<OrderIntent> PlaceOrderAsync(string deploymentId, OrderRequest request, CancellationToken ct = default)
        {
            string krakenPair = KrakenSymbolMap.ToKrakenPair(request.Symbol);
            string type = request.Side == OrderSide.Buy ? "buy" : "sell";
            string orderType = request.Type switch
            {
                OrderType.Market => "market",
                OrderType.Limit => "limit",
                OrderType.StopLoss => "stop-loss",
                OrderType.TakeProfit => "take-profit",
                _ => "market"
            };

            var parameters = new Dictionary<string, string>
            {
                { "pair", krakenPair },
                { "type", type },
                { "ordertype", orderType },
                { "volume", request.Qty.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                { "cl_ord_id", request.IntentId }
            };
            // Margin order: Kraken takes integer leverage (absent = spot). The exchange rejects
            // leverage above what the pair supports, surfaced back as a rejected intent.
            if (request.Leverage > 1m)
                parameters["leverage"] = ((int)Math.Round(request.Leverage)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (request.LimitPrice.HasValue)
                parameters["price"] = request.LimitPrice.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (request.StopPrice.HasValue && (request.Type == OrderType.StopLoss))
                parameters["price"] = request.StopPrice.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

            // Attached conditional close (bracket). Kraken supports one close order per entry; prefer the
            // stop-loss for downside protection, else the take-profit. (The second leg, if both are set,
            // is placed separately by the session as an OCO partner.)
            if (request.StopLossPrice.HasValue)
            {
                parameters["close[ordertype]"] = "stop-loss";
                parameters["close[price]"] = request.StopLossPrice.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (request.TakeProfitPrice.HasValue)
            {
                parameters["close[ordertype]"] = "take-profit";
                parameters["close[price]"] = request.TakeProfitPrice.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            var intent = new OrderIntent
            {
                Id = Guid.NewGuid().ToString("N"),
                IntentId = request.IntentId,
                DeploymentId = deploymentId,
                Request = request,
                Status = OrderStatus.Pending,
                PlacedUtc = DateTime.UtcNow
            };

            try
            {
                JObject response = await SendPrivateAsync("/0/private/AddOrder", parameters, ct);
                var errors = response["error"] as JArray;
                if (errors != null && errors.Count > 0)
                {
                    intent.Status = OrderStatus.Rejected;
                    intent.Error = string.Join(", ", errors);
                    return intent;
                }
                var result = response["result"];
                var txids = result?["txid"] as JArray;
                if (txids != null && txids.Count > 0)
                    intent.ExchangeOrderId = (string?)txids[0];
                intent.Status = OrderStatus.Open;
            }
            catch (Exception ex)
            {
                intent.Status = OrderStatus.Rejected;
                intent.Error = ex.Message;
            }

            return intent;
        }

        public async Task CancelOrderAsync(OrderIntent intent, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(intent.ExchangeOrderId))
            {
                intent.Status = OrderStatus.Cancelled;
                return;
            }
            try
            {
                var parameters = new Dictionary<string, string>
                {
                    { "txid", intent.ExchangeOrderId }
                };
                JObject response = await SendPrivateAsync("/0/private/CancelOrder", parameters, ct);
                var errors = response["error"] as JArray;
                if (errors == null || errors.Count == 0)
                    intent.Status = OrderStatus.Cancelled;
            }
            catch (Exception ex)
            {
                intent.Error = ex.Message;
            }
        }

        public async Task<JObject?> QueryOrdersAsync(IEnumerable<string> exchangeOrderIds, CancellationToken ct = default)
        {
            var parameters = new Dictionary<string, string>
            {
                { "txid", string.Join(",", exchangeOrderIds) }
            };
            return await SendPrivateAsync("/0/private/QueryOrders", parameters, ct);
        }

        public async Task<IReadOnlyList<ExchangeFill>> QueryFillsAsync(IEnumerable<string> exchangeOrderIds, CancellationToken ct = default)
        {
            var ids = exchangeOrderIds.ToList();
            if (ids.Count == 0) return Array.Empty<ExchangeFill>();
            var response = await QueryOrdersAsync(ids, ct);
            return response == null ? Array.Empty<ExchangeFill>() : ParseFills(response);
        }

        /// <summary>
        /// Map a Kraken QueryOrders response into cumulative <see cref="ExchangeFill"/>s. Pure so it
        /// can be unit-tested against canned responses. Average price is cost/vol_exec (the true
        /// executed average); an order counts as Closed once it is closed/canceled/expired.
        /// </summary>
        public static IReadOnlyList<ExchangeFill> ParseFills(JObject response)
        {
            var list = new List<ExchangeFill>();
            if (response["result"] is not JObject result) return list;

            foreach (var prop in result.Properties())
            {
                if (prop.Value is not JObject o) continue;
                decimal volExec = ParseDecimal(o["vol_exec"]);
                decimal cost = ParseDecimal(o["cost"]);
                decimal fee = ParseDecimal(o["fee"]);
                string status = (string?)o["status"] ?? "";
                var descr = o["descr"] as JObject;
                string type = (string?)descr?["type"] ?? "buy";
                string pair = (string?)descr?["pair"] ?? "";
                decimal avg = volExec > 0m ? cost / volExec : ParseDecimal(o["price"]);
                bool closed = status is "closed" or "canceled" or "expired";

                list.Add(new ExchangeFill
                {
                    ExchangeOrderId = prop.Name,
                    Side = string.Equals(type, "sell", StringComparison.OrdinalIgnoreCase) ? OrderSide.Sell : OrderSide.Buy,
                    CumulativeQty = volExec,
                    AvgPrice = avg,
                    CumulativeFee = fee,
                    Closed = closed,
                    Symbol = pair
                });
            }
            return list;
        }

        private static decimal ParseDecimal(JToken? token)
        {
            if (token == null) return 0m;
            return decimal.TryParse((string?)token, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0m;
        }

        private async Task<JObject> SendPrivateAsync(string path, Dictionary<string, string> parameters, CancellationToken ct)
        {
            long nonce = await nonceStore.NextAsync(ct);
            parameters["nonce"] = nonce.ToString();
            string postData = string.Join("&", parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

            using var sha = SHA256.Create();
            byte[] noncePost = sha.ComputeHash(Encoding.UTF8.GetBytes(nonce + postData));
            byte[] pathBytes = Encoding.UTF8.GetBytes(path);
            byte[] signatureInput = new byte[pathBytes.Length + noncePost.Length];
            Buffer.BlockCopy(pathBytes, 0, signatureInput, 0, pathBytes.Length);
            Buffer.BlockCopy(noncePost, 0, signatureInput, pathBytes.Length, noncePost.Length);

            using var hmac = new HMACSHA512(apiSecret);
            string signature = Convert.ToBase64String(hmac.ComputeHash(signatureInput));

            using var req = new HttpRequestMessage(HttpMethod.Post, ApiBase + path);
            req.Headers.Add("API-Key", apiKey);
            req.Headers.Add("API-Sign", signature);
            req.Content = new StringContent(postData, Encoding.UTF8, "application/x-www-form-urlencoded");

            var resp = await httpClient.SendAsync(req, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"Kraken {path} {resp.StatusCode}: {body}");
            return JObject.Parse(body);
        }
    }
}
