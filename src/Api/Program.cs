using System.Text.RegularExpressions;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
var connectionString = builder.Configuration.GetConnectionString("Default") ?? throw new Exception("String de Conexão não configurada!");

app.MapGet("/", () => Results.Ok(new { name = "Trading Platform - SOLID Lab", warning = "Codigo intencionalmente monólito para prática de refatoração" }));

app.MapPost("/signup", async (SignupInput input) =>
{
    await using var cn = new NpgsqlConnection(connectionString); await cn.OpenAsync();
    await using (var check = new NpgsqlCommand("select 1 from app.account where lower(email)=lower(@email)", cn))
    { check.Parameters.AddWithValue("email", input.Email); if (await check.ExecuteScalarAsync() != null) return Results.Conflict("Email already exists"); }

    var id = Guid.NewGuid();
    await using var cmd = new NpgsqlCommand("insert into app.account(account_id,name,email,document,password) values(@id,@name,@email,@document,@password)", cn);
    cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("name", input.Name.Trim()); cmd.Parameters.AddWithValue("email", input.Email.Trim().ToLowerInvariant());
    cmd.Parameters.AddWithValue("document", OnlyNumbers(input.Document)); cmd.Parameters.AddWithValue("password", BCrypt.Net.BCrypt.HashPassword(input.Password)); await cmd.ExecuteNonQueryAsync();
    return Results.Ok(new { accountId = id });
});

app.MapPost("/deposit", async (BalanceInput input) =>
{
    if (!AllowedAsset(input.AssetId)) return Results.BadRequest("Only BTC or USD are allowed");
    if (input.Quantity <= 0) return Results.BadRequest("Quantity must be greater than zero");
    await using var cn = new NpgsqlConnection(connectionString); await cn.OpenAsync();
    if (!await AccountExists(cn, input.AccountId)) return Results.NotFound("Account not found");
    await using var cmd = new NpgsqlCommand(@"insert into app.balance(account_id,asset_id,quantity) values(@a,@asset,@q)
        on conflict(account_id,asset_id) do update set quantity=app.balance.quantity+excluded.quantity", cn);
    cmd.Parameters.AddWithValue("a", input.AccountId); cmd.Parameters.AddWithValue("asset", input.AssetId.ToUpperInvariant()); cmd.Parameters.AddWithValue("q", input.Quantity); await cmd.ExecuteNonQueryAsync();
    return Results.NoContent();
});

app.MapPost("/withdraw", async (BalanceInput input) =>
{
    if (!AllowedAsset(input.AssetId)) return Results.BadRequest("Only BTC or USD are allowed");
    if (input.Quantity <= 0) return Results.BadRequest("Quantity must be greater than zero");
    await using var cn = new NpgsqlConnection(connectionString); await cn.OpenAsync();
    if (!await AccountExists(cn, input.AccountId)) return Results.NotFound("Account not found");
    var available = await AvailableBalance(cn, input.AccountId, input.AssetId.ToUpperInvariant());
    if (available < input.Quantity) return Results.BadRequest(new { error = "Insufficient available balance", available });
    await using var cmd = new NpgsqlCommand("update app.balance set quantity=quantity-@q where account_id=@a and asset_id=@asset", cn);
    cmd.Parameters.AddWithValue("q", input.Quantity); cmd.Parameters.AddWithValue("a", input.AccountId); cmd.Parameters.AddWithValue("asset", input.AssetId.ToUpperInvariant()); await cmd.ExecuteNonQueryAsync();
    return Results.NoContent();
});

app.MapGet("/accounts/{accountId:guid}", async (Guid accountId) =>
{
    await using var cn = new NpgsqlConnection(connectionString); await cn.OpenAsync();
    await using var cmd = new NpgsqlCommand("select account_id,name,email,document from app.account where account_id=@id", cn); cmd.Parameters.AddWithValue("id", accountId);
    await using var r = await cmd.ExecuteReaderAsync(); if (!await r.ReadAsync()) return Results.NotFound("Account not found");
    var account = new { accountId = r.GetGuid(0), name = r.GetString(1), email = r.GetString(2), document = r.GetString(3) }; await r.CloseAsync();
    var assets = new List<object>(); await using var b = new NpgsqlCommand("select asset_id,quantity from app.balance where account_id=@id order by asset_id", cn); b.Parameters.AddWithValue("id", accountId);
    await using var br = await b.ExecuteReaderAsync(); while (await br.ReadAsync()) assets.Add(new { assetId = br.GetString(0), quantity = br.GetDecimal(1) });
    return Results.Ok(new { account.accountId, account.name, account.email, account.document, assets });
});

app.MapPost("/place_order", async (PlaceOrderInput input) =>
{
    var side = (input.Side ?? "").ToLowerInvariant(); if (side != "buy" && side != "sell") return Results.BadRequest("Side must be buy or sell");
    if (input.Quantity <= 0 || input.Price <= 0) return Results.BadRequest("Quantity and price must be greater than zero");
    var market = ParseMarket(input.MarketId); if (market == null) return Results.BadRequest("Invalid market. Example: BTC-USD");
    if (!AllowedAsset(market.Value.Base) || !AllowedAsset(market.Value.Quote) || market.Value.Base == market.Value.Quote) return Results.BadRequest("Market assets must be BTC/USD");

    await using var cn = new NpgsqlConnection(connectionString); await cn.OpenAsync(); if (!await AccountExists(cn, input.AccountId)) return Results.NotFound("Account not found");
    var requiredAsset = side == "sell" ? market.Value.Base : market.Value.Quote; var required = side == "sell" ? input.Quantity : input.Quantity * input.Price;
    var available = await AvailableBalance(cn, input.AccountId, requiredAsset); if (available < required) return Results.BadRequest(new { error = "Insufficient available balance", assetId = requiredAsset, required, available });

    var orderId = Guid.NewGuid(); await using (var cmd = new NpgsqlCommand(@"insert into app.""order""(order_id,market_id,account_id,side,quantity,price,fill_quantity,fill_price,status,timestamp)
        values(@id,@m,@a,@s,@q,@p,0,0,'open',@ts)", cn))
    { cmd.Parameters.AddWithValue("id", orderId); cmd.Parameters.AddWithValue("m", input.MarketId.ToUpperInvariant()); cmd.Parameters.AddWithValue("a", input.AccountId); cmd.Parameters.AddWithValue("s", side); cmd.Parameters.AddWithValue("q", input.Quantity); cmd.Parameters.AddWithValue("p", input.Price); cmd.Parameters.AddWithValue("ts", DateTime.UtcNow); await cmd.ExecuteNonQueryAsync(); }
    await MatchOrder(connectionString, orderId);
    return Results.Ok(new { orderId });
});

app.MapPost("/execute_order/{orderId:guid}", async (Guid orderId) => { await MatchOrder(connectionString, orderId); return Results.NoContent(); });

app.MapPost("/cancel_order", async (CancelOrderInput input) =>
{
    await using var cn = new NpgsqlConnection(connectionString); await cn.OpenAsync();
    await using var cmd = new NpgsqlCommand("update app.\"order\" set status='cancelled' where order_id=@id and account_id=@a and status='open'", cn); cmd.Parameters.AddWithValue("id", input.OrderId); cmd.Parameters.AddWithValue("a", input.AccountId);
    return await cmd.ExecuteNonQueryAsync() == 0 ? Results.BadRequest("Open order not found for account") : Results.NoContent();
});

app.MapGet("/orders/{orderId:guid}", async (Guid orderId) =>
{
    await using var cn = new NpgsqlConnection(connectionString); await cn.OpenAsync(); await using var cmd = new NpgsqlCommand("select order_id,market_id,account_id,side,quantity,price,fill_quantity,fill_price,status,timestamp from app.\"order\" where order_id=@id", cn); cmd.Parameters.AddWithValue("id", orderId);
    await using var r = await cmd.ExecuteReaderAsync(); return !await r.ReadAsync() ? Results.NotFound() : Results.Ok(ReadOrder(r));
});

app.MapGet("/accounts/{accountId:guid}/orders", async (Guid accountId, string? status) =>
{
    await using var cn = new NpgsqlConnection(connectionString); await cn.OpenAsync(); if (!await AccountExists(cn, accountId)) return Results.NotFound("Account not found");
    var sql = "select order_id,market_id,account_id,side,quantity,price,fill_quantity,fill_price,status,timestamp from app.\"order\" where account_id=@a" + (string.IsNullOrWhiteSpace(status) ? "" : " and status=@status") + " order by timestamp desc";
    await using var cmd = new NpgsqlCommand(sql, cn); cmd.Parameters.AddWithValue("a", accountId); if (!string.IsNullOrWhiteSpace(status)) cmd.Parameters.AddWithValue("status", status.ToLowerInvariant());
    var list = new List<object>(); await using var r = await cmd.ExecuteReaderAsync(); while (await r.ReadAsync()) list.Add(ReadOrder(r)); return Results.Ok(list);
});

app.MapGet("/markets/{marketId}/trades", async (string marketId) =>
{
    await using var cn = new NpgsqlConnection(connectionString); await cn.OpenAsync(); await using var cmd = new NpgsqlCommand("select trade_id,buy_order_id,sell_order_id,side,quantity,price,timestamp from app.trade where market_id=@m order by timestamp desc", cn); cmd.Parameters.AddWithValue("m", marketId.ToUpperInvariant());
    var list = new List<object>(); await using var r = await cmd.ExecuteReaderAsync(); while (await r.ReadAsync()) list.Add(new { tradeId=r.GetGuid(0), buyOrderId=r.GetGuid(1), sellOrderId=r.GetGuid(2), side=r.GetString(3), quantity=r.GetDecimal(4), price=r.GetDecimal(5), timestamp=r.GetDateTime(6) }); return Results.Ok(list);
});

app.MapGet("/markets/{marketId}/depth", async (string marketId, int? precision) =>
{
    var p = precision ?? 0; if (p < 0 || p > 10) return Results.BadRequest("Precision must be between 0 and 10"); decimal factor = 1; for (var i=0;i<p;i++) factor*=10;
    await using var cn = new NpgsqlConnection(connectionString); await cn.OpenAsync(); await using var cmd = new NpgsqlCommand("select side,quantity-fill_quantity,price from app.\"order\" where market_id=@m and status='open' and quantity>fill_quantity", cn); cmd.Parameters.AddWithValue("m", marketId.ToUpperInvariant());
    var buys=new Dictionary<decimal,decimal>(); var sells=new Dictionary<decimal,decimal>(); await using var r=await cmd.ExecuteReaderAsync(); while(await r.ReadAsync()) { var side=r.GetString(0); var qty=r.GetDecimal(1); var price=r.GetDecimal(2); var grouped=factor==1?price:Math.Floor(price/factor)*factor; var d=side=="buy"?buys:sells; d[grouped]=d.GetValueOrDefault(grouped)+qty; }
    return Results.Ok(new { buys=buys.OrderByDescending(x=>x.Key).Select(x=>new {quantity=x.Value,price=x.Key}), sells=sells.OrderBy(x=>x.Key).Select(x=>new {quantity=x.Value,price=x.Key}) });
});

app.MapGet("/markets/{marketId}", async (string marketId, DateTime? startDate, DateTime? endDate) =>
{
    await using var cn=new NpgsqlConnection(connectionString); await cn.OpenAsync(); decimal? bestBuy=null,bestSell=null;
    await using(var c=new NpgsqlCommand("select max(price) filter(where side='buy'), min(price) filter(where side='sell') from app.\"order\" where market_id=@m and status='open'",cn)){c.Parameters.AddWithValue("m",marketId.ToUpperInvariant()); await using var r=await c.ExecuteReaderAsync(); if(await r.ReadAsync()){bestBuy=r.IsDBNull(0)?null:r.GetDecimal(0);bestSell=r.IsDBNull(1)?null:r.GetDecimal(1);} }
    var from=startDate?.ToUniversalTime()??DateTime.UnixEpoch; var to=endDate?.ToUniversalTime()??DateTime.UtcNow;
    await using var cmd=new NpgsqlCommand("select min(price),max(price),coalesce(sum(quantity*price),0) from app.trade where market_id=@m and timestamp between @f and @t",cn);cmd.Parameters.AddWithValue("m",marketId.ToUpperInvariant());cmd.Parameters.AddWithValue("f",from);cmd.Parameters.AddWithValue("t",to);
    await using var rr=await cmd.ExecuteReaderAsync();await rr.ReadAsync(); return Results.Ok(new { spread=bestBuy.HasValue&&bestSell.HasValue?bestSell-bestBuy:null, min=rr.IsDBNull(0)?null:rr.GetDecimal(0), max=rr.IsDBNull(1)?null:rr.GetDecimal(1), volume=rr.GetDecimal(2) });
});

app.Run();

static async Task MatchOrder(string cs, Guid incomingId)
{
    await using var cn=new NpgsqlConnection(cs); await cn.OpenAsync(); await using var tx=await cn.BeginTransactionAsync();
    while(true)
    {
        OrderRow? incoming=null; await using(var c=new NpgsqlCommand("select order_id,market_id,account_id,side,quantity,price,fill_quantity,timestamp from app.\"order\" where order_id=@id and status='open' for update",cn,tx)){c.Parameters.AddWithValue("id",incomingId);await using var r=await c.ExecuteReaderAsync();if(await r.ReadAsync())incoming=new(r.GetGuid(0),r.GetString(1),r.GetGuid(2),r.GetString(3),r.GetDecimal(4),r.GetDecimal(5),r.GetDecimal(6),r.GetDateTime(7));}
        if(incoming==null)break;
        var opposite=incoming.Side=="buy"?"sell":"buy";var orderBy=opposite=="sell"?"price asc, timestamp asc":"price desc, timestamp asc"; OrderRow? maker=null;
        await using(var c=new NpgsqlCommand($"select order_id,market_id,account_id,side,quantity,price,fill_quantity,timestamp from app.\"order\" where market_id=@m and side=@s and status='open' and order_id<>@id and account_id<>@a order by {orderBy} limit 1 for update",cn,tx)){c.Parameters.AddWithValue("m",incoming.Market);c.Parameters.AddWithValue("s",opposite);c.Parameters.AddWithValue("id",incoming.Id);c.Parameters.AddWithValue("a",incoming.Account);await using var r=await c.ExecuteReaderAsync();if(await r.ReadAsync())maker=new(r.GetGuid(0),r.GetString(1),r.GetGuid(2),r.GetString(3),r.GetDecimal(4),r.GetDecimal(5),r.GetDecimal(6),r.GetDateTime(7));}
        if(maker==null)break; var buy=incoming.Side=="buy"?incoming:maker;var sell=incoming.Side=="sell"?incoming:maker;if(buy.Price<sell.Price)break;
        var qty=Math.Min(incoming.Quantity-incoming.Filled,maker.Quantity-maker.Filled);var tradePrice=maker.Timestamp<=incoming.Timestamp?maker.Price:incoming.Price;var buyOrder=buy;var sellOrder=sell;
        var parsed=ParseMarket(incoming.Market)!.Value; var cost=qty*tradePrice;
        await ChangeBalance(cn,tx,buyOrder.Account,parsed.Quote,-cost); await ChangeBalance(cn,tx,buyOrder.Account,parsed.Base,qty); await ChangeBalance(cn,tx,sellOrder.Account,parsed.Base,-qty); await ChangeBalance(cn,tx,sellOrder.Account,parsed.Quote,cost);
        await UpdateFill(cn,tx,incoming.Id,incoming.Filled+qty,tradePrice,incoming.Quantity); await UpdateFill(cn,tx,maker.Id,maker.Filled+qty,tradePrice,maker.Quantity);
        await using var t=new NpgsqlCommand("insert into app.trade(trade_id,market_id,buy_order_id,sell_order_id,side,quantity,price,timestamp) values(@id,@m,@b,@s,@side,@q,@p,@ts)",cn,tx);t.Parameters.AddWithValue("id",Guid.NewGuid());t.Parameters.AddWithValue("m",incoming.Market);t.Parameters.AddWithValue("b",buyOrder.Id);t.Parameters.AddWithValue("s",sellOrder.Id);t.Parameters.AddWithValue("side",incoming.Side);t.Parameters.AddWithValue("q",qty);t.Parameters.AddWithValue("p",tradePrice);t.Parameters.AddWithValue("ts",DateTime.UtcNow);await t.ExecuteNonQueryAsync();
    }
    await tx.CommitAsync();
}
static async Task UpdateFill(NpgsqlConnection cn,NpgsqlTransaction tx,Guid id,decimal filled,decimal price,decimal total){await using var c=new NpgsqlCommand("update app.\"order\" set fill_quantity=@f,fill_price=@p,status=case when @f>=@total then 'filled' else 'open' end where order_id=@id",cn,tx);c.Parameters.AddWithValue("f",filled);c.Parameters.AddWithValue("p",price);c.Parameters.AddWithValue("total",total);c.Parameters.AddWithValue("id",id);await c.ExecuteNonQueryAsync();}
static async Task ChangeBalance(NpgsqlConnection cn,NpgsqlTransaction tx,Guid account,string asset,decimal delta){await using var c=new NpgsqlCommand("insert into app.balance(account_id,asset_id,quantity) values(@a,@asset,@d) on conflict(account_id,asset_id) do update set quantity=app.balance.quantity+excluded.quantity",cn,tx);c.Parameters.AddWithValue("a",account);c.Parameters.AddWithValue("asset",asset);c.Parameters.AddWithValue("d",delta);await c.ExecuteNonQueryAsync();}
static async Task<bool> AccountExists(NpgsqlConnection cn,Guid id){await using var c=new NpgsqlCommand("select 1 from app.account where account_id=@id",cn);c.Parameters.AddWithValue("id",id);return await c.ExecuteScalarAsync()!=null;}
static async Task<decimal> AvailableBalance(NpgsqlConnection cn,Guid account,string asset){decimal balance=0;await using(var c=new NpgsqlCommand("select quantity from app.balance where account_id=@a and asset_id=@asset",cn)){c.Parameters.AddWithValue("a",account);c.Parameters.AddWithValue("asset",asset);var v=await c.ExecuteScalarAsync();if(v!=null)balance=(decimal)v;} decimal reserved=0;await using(var c=new NpgsqlCommand("select market_id,side,quantity-fill_quantity,price from app.\"order\" where account_id=@a and status='open'",cn)){c.Parameters.AddWithValue("a",account);await using var r=await c.ExecuteReaderAsync();while(await r.ReadAsync()){var m=ParseMarket(r.GetString(0));if(m==null)continue;var side=r.GetString(1);var remaining=r.GetDecimal(2);var price=r.GetDecimal(3);if(side=="sell"&&m.Value.Base==asset)reserved+=remaining;if(side=="buy"&&m.Value.Quote==asset)reserved+=remaining*price;}}return balance-reserved;}
static object ReadOrder(NpgsqlDataReader r)=>new{orderId=r.GetGuid(0),marketId=r.GetString(1),accountId=r.GetGuid(2),side=r.GetString(3),quantity=r.GetDecimal(4),price=r.GetDecimal(5),fillQuantity=r.GetDecimal(6),fillPrice=r.GetDecimal(7),status=r.GetString(8),timestamp=r.GetDateTime(9)};
static (string Base,string Quote)? ParseMarket(string? market){if(string.IsNullOrWhiteSpace(market))return null;var p=market.ToUpperInvariant().Split('-');return p.Length==2?(p[0],p[1]):null;}
static bool AllowedAsset(string? asset)=>asset?.ToUpperInvariant() is "BTC" or "USD";
static string OnlyNumbers(string? s)=>new((s??"").Where(char.IsDigit).ToArray());
static bool ValidCpf(string? cpf){var s=OnlyNumbers(cpf);if(s.Length!=11||s.Distinct().Count()==1)return false;int Calc(int len){var sum=0;for(int i=0;i<len;i++)sum+=(s[i]-'0')*(len+1-i);var rem=(sum*10)%11;return rem==10?0:rem;}return Calc(9)==s[9]-'0'&&Calc(10)==s[10]-'0';}

record SignupInput(string Name,string Email,string Document,string Password);
record BalanceInput(Guid AccountId,string AssetId,decimal Quantity);
record PlaceOrderInput(string MarketId,Guid AccountId,string Side,decimal Quantity,decimal Price);
record CancelOrderInput(Guid AccountId,Guid OrderId);
record OrderRow(Guid Id,string Market,Guid Account,string Side,decimal Quantity,decimal Price,decimal Filled,DateTime Timestamp);
