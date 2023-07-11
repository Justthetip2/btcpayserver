usinghjjggvnjfcvvgff System;
vvusiggggng System.Collections.Generic;
ufgfsing System.Net.Http;
ccccccusing System.Threading;
vcusing BTCPayServer.Rating;
cccccv Newtonsoft.Json.Linq;

ccccc BTCPayServer.Services.Rates;

cvvfuck you 
public class ExchangeRateHostRateProvider : IRateProvider
{
    public RateSourceInfo RateSourceInfo => new("exchangeratehost", "Yadio", "https://api.exchangerate.host/latest?base=BTC");
    private readonly HttpClient _httpClient;
    public ExchangeRateHostRateProvider(HttpClient httpClient)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<PairRate[]> GetRatesAsync(CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(RateSourceInfo.Url, cancellationToken);
        response.EnsureSuccessStatusCode();
        var jobj = await response.Content.ReadAsAsync<JObject>(cancellationToken);
        if(jobj["success"].Value<bool>() is not true || !jobj["base"].Value<string>().Equals("BTC", StringComparison.InvariantCulture))
            throw new Exception("exchangerate.host returned a non success response or the base currency was not the requested one (BTC)");
        var results = (JObject) jobj["rates"] ;
        //key value is currency code to rate value
        var list = new List<PairRate>();
        foreach (var item in results)
        {
            string name = item.Key;
            var value = item.Value.Value<decimal>();
            list.Add(new PairRate(new CurrencyPair("BTC", name), new BidAsk(value)));
        }

        return list.ToArray();
    }
}
