//+------------------------------------------------------------------+
//|                                                Mt5Bridge.mq5      |
//|  Native-side JSON-RPC bridge for the tradecop001 MT5 adapter.     |
//|  Listens on 127.0.0.1:5052 only. See README.md in this folder.    |
//+------------------------------------------------------------------+
#property strict
#property version   "1.00"
#property description "Loopback-only JSON-RPC bridge for the tradecop001 MT5 adapter (demo accounts)."

#define AF_INET            2
#define SOCK_STREAM        1
#define IPPROTO_TCP        6
#define FIONBIO            0x8004667E
#define INVALID_SOCKET32   0xFFFFFFFF
#define INVALID_SOCKET64   0xFFFFFFFFFFFFFFFF
#define SOCKET_ERROR       (-1)
#define WSAEWOULDBLOCK     10035
#define LOOPBACK_ADDRESS   0x100007f   // 127.0.0.1, network byte order

struct sockaddr
  {
   short             family;
   ushort            port;
   uint              address;
   ulong             ignore;
  };

// Native MQL5 sockets (SocketCreate/SocketConnect/...) are outbound-only:
// there is no SocketBind/SocketListen/SocketAccept. To host the listener the
// EA therefore imports Winsock directly (Windows MT5, "Allow DLL imports").
// No remote address is ever used: bind is hardcoded to 127.0.0.1.
#import "ws2_32.dll"
   int   WSAStartup(uint wVersionRequested, uchar &lpWSAData[]);

   uint  socket(int af, int type, int protocol);
   ulong socket(int af, int type, uint protocol);
   int   bind(uint s, sockaddr &addr, int namelen);
   int   bind(ulong s, sockaddr &addr, int namelen);
   int   listen(uint s, int backlog);
   int   listen(ulong s, int backlog);
   uint  accept(uint s, int addr, int addrlen);
   ulong accept(ulong s, int addr, int addrlen);
   int   send(uint s, uchar &buf[], int len, int flags);
   int   send(ulong s, uchar &buf[], int len, int flags);
   int   recv(uint s, uchar &buf[], int len, int flags);
   int   recv(ulong s, uchar &buf[], int len, int flags);
   int   ioctlsocket(uint s, uint cmd, uint &arg);
   int   ioctlsocket(ulong s, uint cmd, uint &arg);
   int   shutdown(uint s, int how);
   int   shutdown(ulong s, int how);
   int   closesocket(uint s);
   int   closesocket(ulong s);
   ushort htons(ushort hostshort);
   int   WSAGetLastError();
#import

input int    InpPort         = 5052;       // Bridge listener port (loopback only)
input int    InpTimerMs      = 100;        // Socket poll interval (ms)
input long   InpMagic        = 773311;     // Magic number for orders sent by the bridge
input bool   InpAllowNonDemo = false;      // Allow trading on a non-demo account (explicit override)

bool     gIs64          = false;
uint     gServer32      = 0;
ulong    gServer64      = 0;
bool     gServerReady   = false;
uint     gClient32      = 0;
ulong    gClient64      = 0;
bool     gClientValid   = false;
string   gPending       = "";

string   gBarSymbols[];
string   gBarIntervals[];
datetime gBarLastTimes[];
string   gTickSymbols[];

//+------------------------------------------------------------------+
//| Lifetime                                                         |
//+------------------------------------------------------------------+
int OnInit()
  {
   if(InpPort < 1 || InpPort > 65535)
     {
      Print("Mt5Bridge: InpPort must be between 1 and 65535.");
      return(INIT_PARAMETERS_INCORRECT);
     }

   gIs64 = (TerminalInfoInteger(TERMINAL_X64) != 0);

   uchar wsaData[];
   ArrayResize(wsaData, 4096);
   if(WSAStartup(0x0202, wsaData) != 0)
     {
      Print("Mt5Bridge: WSAStartup failed, error ", WSAGetLastError());
      return(INIT_FAILED);
     }

   if(!ServerCreate())
      return(INIT_FAILED);

   if(AccountInfoInteger(ACCOUNT_TRADE_MODE) != ACCOUNT_TRADE_MODE_DEMO && !InpAllowNonDemo)
      Print("Mt5Bridge: WARNING - non-demo account detected; trading ops are blocked unless InpAllowNonDemo=true.");

   EventSetMillisecondTimer(InpTimerMs);
   Print("Mt5Bridge: listening on 127.0.0.1:", InpPort);
   return(INIT_SUCCEEDED);
  }

void OnDeinit(const int reason)
  {
   EventKillTimer();
   CloseClient();
   ServerClose();
  }

//+------------------------------------------------------------------+
//| Winsock listener helpers                                         |
//+------------------------------------------------------------------+
bool ServerCreate()
  {
   sockaddr addr;
   ZeroMemory(addr);
   addr.family  = AF_INET;
   addr.port    = htons((ushort)InpPort);
   addr.address = LOOPBACK_ADDRESS;

   uint nonblock = 1;

   if(gIs64)
     {
      uint protocol = IPPROTO_TCP;
      gServer64 = socket(AF_INET, SOCK_STREAM, protocol);
      if(gServer64 == INVALID_SOCKET64)
        {
         Print("Mt5Bridge: socket() failed, error ", WSAGetLastError());
         return(false);
        }
      ioctlsocket(gServer64, FIONBIO, nonblock);
      if(bind(gServer64, addr, sizeof(addr)) != 0)
        {
         Print("Mt5Bridge: bind() failed, error ", WSAGetLastError());
         closesocket(gServer64);
         return(false);
        }
      if(listen(gServer64, 8) != 0)
        {
         Print("Mt5Bridge: listen() failed, error ", WSAGetLastError());
         closesocket(gServer64);
         return(false);
        }
     }
   else
     {
      int protocol = IPPROTO_TCP;
      gServer32 = socket(AF_INET, SOCK_STREAM, protocol);
      if(gServer32 == INVALID_SOCKET32)
        {
         Print("Mt5Bridge: socket() failed, error ", WSAGetLastError());
         return(false);
        }
      ioctlsocket(gServer32, FIONBIO, nonblock);
      if(bind(gServer32, addr, sizeof(addr)) != 0)
        {
         Print("Mt5Bridge: bind() failed, error ", WSAGetLastError());
         closesocket(gServer32);
         return(false);
        }
      if(listen(gServer32, 8) != 0)
        {
         Print("Mt5Bridge: listen() failed, error ", WSAGetLastError());
         closesocket(gServer32);
         return(false);
        }
     }

   gServerReady = true;
   return(true);
  }

void ServerClose()
  {
   if(!gServerReady)
      return;

   if(gIs64) closesocket(gServer64);
   else      closesocket(gServer32);

   gServerReady = false;
  }

void AcceptClient()
  {
   if(!gServerReady || gClientValid)
      return;

   uint nonblock = 1;

   if(gIs64)
     {
      ulong accepted = accept(gServer64, 0, 0);
      if(accepted == INVALID_SOCKET64)
         return;
      gClient64 = accepted;
      ioctlsocket(gClient64, FIONBIO, nonblock);
     }
   else
     {
      uint accepted = accept(gServer32, 0, 0);
      if(accepted == INVALID_SOCKET32)
         return;
      gClient32 = accepted;
      ioctlsocket(gClient32, FIONBIO, nonblock);
     }

   gClientValid = true;
   gPending = "";
   Print("Mt5Bridge: adapter connected.");
  }

void CloseClient()
  {
   if(!gClientValid)
      return;

   if(gIs64)
     {
      shutdown(gClient64, 2);
      closesocket(gClient64);
     }
   else
     {
      shutdown(gClient32, 2);
      closesocket(gClient32);
     }

   gClientValid = false;
   gPending = "";
   ArrayResize(gBarSymbols, 0);
   ArrayResize(gBarIntervals, 0);
   ArrayResize(gBarLastTimes, 0);
   ArrayResize(gTickSymbols, 0);
   Print("Mt5Bridge: adapter disconnected.");
  }

void ReadClient()
  {
   if(!gClientValid)
      return;

   uchar buffer[];
   ArrayResize(buffer, 8192);
   int received = gIs64 ? recv(gClient64, buffer, 8192, 0)
                        : recv(gClient32, buffer, 8192, 0);

   if(received > 0)
     {
      gPending += CharArrayToString(buffer, 0, received, CP_UTF8);
      ProcessLines();
     }
   else if(received == 0)
      CloseClient();
   else if(WSAGetLastError() != WSAEWOULDBLOCK)
      CloseClient();
  }

bool SendLine(string line)
  {
   if(!gClientValid)
      return(false);

   string payload = line + "\n";
   uchar  buffer[];
   int    length = StringToCharArray(payload, buffer, 0, WHOLE_ARRAY, CP_UTF8) - 1;
   if(length <= 0)
      return(true);

   int offset = 0;
   while(offset < length)
     {
      int sent = gIs64 ? send(gClient64, buffer, length - offset, 0)
                       : send(gClient32, buffer, length - offset, 0);

      if(sent == SOCKET_ERROR)
        {
         if(WSAGetLastError() == WSAEWOULDBLOCK)
            continue;
         CloseClient();
         return(false);
        }
      if(sent <= 0)
        {
         CloseClient();
         return(false);
        }

      offset += sent;
      if(offset < length)
         ArrayCopy(buffer, buffer, 0, sent, length - offset);
     }

   return(true);
  }

void ProcessLines()
  {
   while(gClientValid)
     {
      int newline = StringFind(gPending, "\n");
      if(newline < 0)
         break;

      string line = StringSubstr(gPending, 0, newline);
      gPending = StringSubstr(gPending, newline + 1);
      StringTrimRight(line);
      if(StringLen(line) > 0)
         HandleLine(line);
     }
  }

//+------------------------------------------------------------------+
//| Minimal JSON helpers (MQL5 has no built-in JSON parser)           |
//+------------------------------------------------------------------+
int FindJsonKey(string json, string key)
  {
   string quoted = "\"" + key + "\"";
   StringToLower(quoted);
   string lowered = json;
   StringToLower(lowered);
   return(StringFind(lowered, quoted));
  }

int JsonValueStart(string json, int keyPos)
  {
   int colon = StringFind(json, ":", keyPos + 1);
   if(colon < 0)
      return(-1);

   int i = colon + 1;
   while(i < StringLen(json) && StringGetCharacter(json, i) == ' ')
      i++;
   return(i);
  }

string JsonString(string json, string key, string fallback = "")
  {
   int keyPos = FindJsonKey(json, key);
   if(keyPos < 0)
      return(fallback);

   int i = JsonValueStart(json, keyPos);
   if(i < 0 || StringGetCharacter(json, i) != '"')
      return(fallback);

   string result = "";
   int    length = StringLen(json);
   i++;
   while(i < length)
     {
      ushort ch = StringGetCharacter(json, i);
      if(ch == '"')
         break;
      if(ch == '\\')
        {
         i++;
         if(i >= length)
            break;
         ushort escaped = StringGetCharacter(json, i);
         if(escaped == 'n')       result += "\n";
         else if(escaped == 't')  result += "\t";
         else if(escaped == 'r')  result += "\r";
         else                     result += CharToString((uchar)escaped);
        }
      else
         result += CharToString((uchar)ch);
      i++;
     }

   return(result);
  }

double JsonNumber(string json, string key, double fallback = 0.0)
  {
   int keyPos = FindJsonKey(json, key);
   if(keyPos < 0)
      return(fallback);

   int i = JsonValueStart(json, keyPos);
   if(i < 0)
      return(fallback);

   string number = "";
   int    length = StringLen(json);
   while(i < length)
     {
      ushort ch = StringGetCharacter(json, i);
      bool numeric = (ch >= '0' && ch <= '9') || ch == '-' || ch == '+' ||
                     ch == '.' || ch == 'e' || ch == 'E';
      if(!numeric)
         break;
      number += CharToString((uchar)ch);
      i++;
     }

   return(number == "" ? fallback : StringToDouble(number));
  }

string JsonEscape(string value)
  {
   StringReplace(value, "\\", "\\\\");
   StringReplace(value, "\"", "\\\"");
   StringReplace(value, "\n", "\\n");
   StringReplace(value, "\r", "\\r");
   return(value);
  }

string Num(double value, int digits = 8)
  {
   string text = DoubleToString(value, digits);
   while(StringFind(text, ".") >= 0 && StringLen(text) > 0 &&
         StringGetCharacter(text, StringLen(text) - 1) == '0')
      text = StringSubstr(text, 0, StringLen(text) - 1);

   if(StringLen(text) > 0 && StringGetCharacter(text, StringLen(text) - 1) == '.')
      text = StringSubstr(text, 0, StringLen(text) - 1);

   return(text == "-0" ? "0" : text);
  }

bool SameOp(string actual, string expected)
  {
   return(StringCompare(actual, expected, false) == 0);
  }

string IsoTime(datetime time)
  {
   long offset = (long)TimeGMT() - (long)TimeCurrent();
   MqlDateTime dt;
   TimeToStruct((datetime)((long)time + offset), dt);
   return(StringFormat("%04d-%02d-%02dT%02d:%02d:%02d+00:00",
                       dt.year, dt.mon, dt.day, dt.hour, dt.min, dt.sec));
  }

ENUM_TIMEFRAMES Timeframe(string interval)
  {
   string value = interval;
   StringToLower(value);
   if(value == "m5" || value == "5 mins")    return(PERIOD_M5);
   if(value == "m15" || value == "15 mins")  return(PERIOD_M15);
   if(value == "h1" || value == "1 hour")    return(PERIOD_H1);
   if(value == "d1" || value == "1 day")     return(PERIOD_D1);
   return(PERIOD_M1);
  }

//+------------------------------------------------------------------+
//| Result payload builders                                          |
//+------------------------------------------------------------------+
string OrderResultJson(string brokerOrderId, string clientOrderId, string status,
                       double filled, double averagePrice, string message = "")
  {
   string client = StringLen(clientOrderId) > 0
                   ? "\"" + JsonEscape(clientOrderId) + "\""
                   : "null";
   string detail = StringLen(message) > 0
                   ? ",\"message\":\"" + JsonEscape(message) + "\""
                   : "";
   return(StringFormat("{\"brokerOrderId\":\"%s\",\"clientOrderId\":%s,\"status\":\"%s\",\"filledQuantity\":%s,\"averageFillPrice\":%s%s}",
                       JsonEscape(brokerOrderId), client, JsonEscape(status),
                       Num(filled), Num(averagePrice), detail));
  }

string AccountJson()
  {
   return(StringFormat("{\"accountId\":\"%s\",\"equity\":%s,\"cash\":%s,\"buyingPower\":%s,\"currency\":\"%s\"}",
                       JsonEscape((string)AccountInfoInteger(ACCOUNT_LOGIN)),
                       Num(AccountInfoDouble(ACCOUNT_EQUITY)),
                       Num(AccountInfoDouble(ACCOUNT_BALANCE)),
                       Num(AccountInfoDouble(ACCOUNT_MARGIN_FREE)),
                       JsonEscape(AccountInfoString(ACCOUNT_CURRENCY))));
  }

string PositionsJson()
  {
   string items = "";
   for(int i = PositionsTotal() - 1; i >= 0; i--)
     {
      ulong ticket = PositionGetTicket(i);
      if(ticket == 0 || !PositionSelectByTicket(ticket))
         continue;

      string symbol = PositionGetString(POSITION_SYMBOL);
      double volume = PositionGetDouble(POSITION_VOLUME);
      if((long)PositionGetInteger(POSITION_TYPE) == POSITION_TYPE_SELL)
         volume = -volume;

      double market = volume >= 0 ? SymbolInfoDouble(symbol, SYMBOL_BID)
                                  : SymbolInfoDouble(symbol, SYMBOL_ASK);

      string item = StringFormat("{\"symbol\":{\"value\":\"%s\",\"exchange\":null},\"quantity\":%s,\"averageEntryPrice\":%s,\"marketPrice\":%s}",
                                 JsonEscape(symbol), Num(volume),
                                 Num(PositionGetDouble(POSITION_PRICE_OPEN)), Num(market));
      if(StringLen(items) > 0)
         items += ",";
      items += item;
     }

   return("[" + items + "]");
  }

string HistoryJson(string symbol, string interval, int count)
  {
   int take = (int)MathMin(MathMax(count, 1), 10000);
   MqlRates rates[];
   ArrayResize(rates, take);
   int copied = CopyRates(symbol, Timeframe(interval), 0, take, rates);

   string items = "";
   for(int i = 0; i < copied; i++)
     {
      string item = StringFormat("{\"symbol\":{\"value\":\"%s\",\"exchange\":null},\"openTime\":\"%s\",\"open\":%s,\"high\":%s,\"low\":%s,\"close\":%s,\"volume\":%s}",
                                 JsonEscape(symbol), IsoTime(rates[i].time),
                                 Num(rates[i].open), Num(rates[i].high), Num(rates[i].low),
                                 Num(rates[i].close), Num((double)rates[i].tick_volume));
      if(StringLen(items) > 0)
         items += ",";
      items += item;
     }

   return("[" + items + "]");
  }

string BarNotificationJson(string symbol, string interval, MqlRates &rate)
  {
   string bar = StringFormat("{\"symbol\":{\"value\":\"%s\",\"exchange\":null},\"openTime\":\"%s\",\"open\":%s,\"high\":%s,\"low\":%s,\"close\":%s,\"volume\":%s}",
                             JsonEscape(symbol), IsoTime(rate.time),
                             Num(rate.open), Num(rate.high), Num(rate.low),
                             Num(rate.close), Num((double)rate.tick_volume));
   return(StringFormat("{\"op\":\"bar\",\"result\":{\"symbol\":\"%s\",\"interval\":\"%s\",\"bar\":%s}}",
                       JsonEscape(symbol), JsonEscape(interval), bar));
  }

string TickNotificationJson(string symbol)
  {
   double bid = SymbolInfoDouble(symbol, SYMBOL_BID);
   double ask = SymbolInfoDouble(symbol, SYMBOL_ASK);
   string tick = StringFormat("{\"symbol\":{\"value\":\"%s\"},\"time\":\"%s\",\"bid\":%s,\"ask\":%s}",
                              JsonEscape(symbol), IsoTime(TimeCurrent()), Num(bid), Num(ask));
   return(StringFormat("{\"op\":\"tick\",\"result\":{\"symbol\":\"%s\",\"tick\":%s}}",
                       JsonEscape(symbol), tick));
  }

//+------------------------------------------------------------------+
//| Trading                                                          |
//+------------------------------------------------------------------+
bool TradingAllowed()
  {
   return(AccountInfoInteger(ACCOUNT_TRADE_MODE) == ACCOUNT_TRADE_MODE_DEMO || InpAllowNonDemo);
  }

string PlaceOrder(string line)
  {
   if(!TradingAllowed())
      return(OrderResultJson("", "", "Rejected", 0, 0, "MT5 bridge is demo-only; set InpAllowNonDemo=true to override."));

   string symbol = JsonString(line, "symbol");
   if(symbol == "")
      return(OrderResultJson("", "", "Rejected", 0, 0, "placeOrder requires 'symbol'."));

   double quantity = JsonNumber(line, "quantity");
   if(quantity <= 0)
      return(OrderResultJson("", "", "Rejected", 0, 0, "placeOrder requires a positive 'quantity'."));

   string side       = JsonString(line, "side", "Buy");
   string type       = JsonString(line, "type", "Market");
   string tif        = JsonString(line, "timeInForce", "Day");
   string clientId   = JsonString(line, "clientOrderId");
   double limitPrice = JsonNumber(line, "limitPrice");
   double stopPrice  = JsonNumber(line, "stopPrice");
   bool   isBuy      = SameOp(side, "Buy");
   StringToLower(type);
   StringToLower(tif);

   MqlTradeRequest request;
   MqlTradeResult  result;
   ZeroMemory(request);
   ZeroMemory(result);

   ENUM_ORDER_TYPE orderType = isBuy ? ORDER_TYPE_BUY : ORDER_TYPE_SELL;
   bool pending = false;
   if(type == "limit")
     {
      orderType = isBuy ? ORDER_TYPE_BUY_LIMIT : ORDER_TYPE_SELL_LIMIT;
      pending   = true;
     }
   else if(type == "stop")
     {
      orderType = isBuy ? ORDER_TYPE_BUY_STOP : ORDER_TYPE_SELL_STOP;
      pending   = true;
     }
   else if(type == "stoplimit")
     {
      orderType = isBuy ? ORDER_TYPE_BUY_STOP_LIMIT : ORDER_TYPE_SELL_STOP_LIMIT;
      pending   = true;
     }

   request.action   = pending ? TRADE_ACTION_PENDING : TRADE_ACTION_DEAL;
   request.symbol   = symbol;
   request.volume   = NormalizeDouble(quantity, 2);
   request.type     = orderType;
   request.magic    = (ulong)InpMagic;
   request.comment  = StringSubstr(clientId, 0, 31);
   request.type_time = (tif == "gtc") ? ORDER_TIME_GTC : ORDER_TIME_DAY;

   if(type == "limit")
      request.price = limitPrice;
   else if(type == "stop")
      request.price = stopPrice;
   else if(type == "stoplimit")
     {
      request.price     = stopPrice;
      request.stoplimit = limitPrice;
     }
   else
      request.price = isBuy ? SymbolInfoDouble(symbol, SYMBOL_ASK)
                            : SymbolInfoDouble(symbol, SYMBOL_BID);

   // Filling policy applies to market deals only.
   if(!pending)
     {
      int filling = (int)SymbolInfoInteger(symbol, SYMBOL_FILLING_MODE);
      if((filling & (int)SYMBOL_FILLING_IOC) != 0)
         request.type_filling = ORDER_FILLING_IOC;
      else if((filling & (int)SYMBOL_FILLING_FOK) != 0)
         request.type_filling = ORDER_FILLING_FOK;
      else
         request.type_filling = ORDER_FILLING_RETURN;
     }

   bool sent = OrderSend(request, result);
   int  code = (int)result.retcode;
   string status = "Rejected";
   string message = sent ? "" : (StringLen(result.comment) > 0 ? result.comment : "OrderSend failed, retcode " + (string)code);

   if(sent)
     {
      if(code == TRADE_RETCODE_DONE_PARTIAL) status = "PartiallyFilled";
      else if(code == TRADE_RETCODE_DONE)    status = "Filled";
      else if(code == TRADE_RETCODE_PLACED)  status = "New";
      else                                  status = "Submitted";
     }

   string brokerId = (string)result.order;
   if(brokerId == "0")
      brokerId = (string)result.deal;

   return(OrderResultJson(brokerId, clientId, status,
                          sent ? result.volume : 0, sent ? result.price : 0, message));
  }

string CancelOrder(string line)
  {
   if(!TradingAllowed())
      return(OrderResultJson("", "", "Rejected", 0, 0, "MT5 bridge is demo-only; set InpAllowNonDemo=true to override."));

   string rawId = JsonString(line, "orderId");
   ulong  ticket = (ulong)StringToInteger(rawId);
   if(ticket == 0)
      return(OrderResultJson(rawId, "", "Rejected", 0, 0, "cancelOrder requires a numeric 'orderId'."));

   MqlTradeRequest request;
   MqlTradeResult  result;
   ZeroMemory(request);
   ZeroMemory(result);

   if(OrderSelect(ticket))
     {
      ENUM_ORDER_TYPE orderType = (ENUM_ORDER_TYPE)OrderGetInteger(ORDER_TYPE);
      bool pending = orderType == ORDER_TYPE_BUY_LIMIT  || orderType == ORDER_TYPE_SELL_LIMIT ||
                     orderType == ORDER_TYPE_BUY_STOP   || orderType == ORDER_TYPE_SELL_STOP  ||
                     orderType == ORDER_TYPE_BUY_STOP_LIMIT || orderType == ORDER_TYPE_SELL_STOP_LIMIT;
      if(!pending)
         return(OrderResultJson(rawId, "", "Rejected", 0, 0, "Order is not a pending order."));

      request.action = TRADE_ACTION_REMOVE;
      request.order  = ticket;
     }
   else if(PositionSelectByTicket(ticket))
     {
      string symbol = PositionGetString(POSITION_SYMBOL);
      bool   isBuy  = (long)PositionGetInteger(POSITION_TYPE) == POSITION_TYPE_BUY;
      request.action = TRADE_ACTION_DEAL;
      request.symbol = symbol;
      request.volume = PositionGetDouble(POSITION_VOLUME);
      request.position = ticket;
      request.type   = isBuy ? ORDER_TYPE_SELL : ORDER_TYPE_BUY;
      request.price  = isBuy ? SymbolInfoDouble(symbol, SYMBOL_BID)
                             : SymbolInfoDouble(symbol, SYMBOL_ASK);
      request.magic  = (ulong)InpMagic;

      int filling = (int)SymbolInfoInteger(symbol, SYMBOL_FILLING_MODE);
      if((filling & (int)SYMBOL_FILLING_IOC) != 0)
         request.type_filling = ORDER_FILLING_IOC;
      else if((filling & (int)SYMBOL_FILLING_FOK) != 0)
         request.type_filling = ORDER_FILLING_FOK;
      else
         request.type_filling = ORDER_FILLING_RETURN;
     }
   else
      return(OrderResultJson(rawId, "", "Rejected", 0, 0, "Order or position not found."));

   bool sent = OrderSend(request, result);
   string status = sent ? "Cancelled" : "Rejected";
   string message = sent ? "" : (StringLen(result.comment) > 0 ? result.comment : "Cancel failed, retcode " + (string)result.retcode);
   return(OrderResultJson(rawId, "", status, 0, 0, message));
  }

//+------------------------------------------------------------------+
//| Request dispatch                                                 |
//+------------------------------------------------------------------+
void SendOk(int id, string result)
  {
   SendLine(StringFormat("{\"id\":%d,\"ok\":true,\"result\":%s}", id, result));
  }

void SendError(int id, string message)
  {
   SendLine(StringFormat("{\"id\":%d,\"ok\":false,\"error\":\"%s\"}", id, JsonEscape(message)));
  }

void HandleLine(string line)
  {
   int    id = (int)JsonNumber(line, "id");
   string op = JsonString(line, "op");

   if(SameOp(op, "account"))
      SendOk(id, AccountJson());
   else if(SameOp(op, "positions"))
      SendOk(id, PositionsJson());
   else if(SameOp(op, "history"))
     {
      string symbol = JsonString(line, "symbol");
      if(symbol == "")
         SendError(id, "history requires 'symbol'.");
      else
         SendOk(id, HistoryJson(symbol, JsonString(line, "interval", "M1"), (int)JsonNumber(line, "count", 100)));
     }
   else if(SameOp(op, "placeOrder"))
      SendOk(id, PlaceOrder(line));
   else if(SameOp(op, "cancelOrder"))
      SendOk(id, CancelOrder(line));
   else if(SameOp(op, "subscribeBars"))
     {
      string symbol = JsonString(line, "symbol");
      if(symbol == "")
         SendError(id, "subscribeBars requires 'symbol'.");
      else
        {
         AddBarSubscription(symbol, JsonString(line, "interval", "M1"));
         SendOk(id, "{\"subscribed\":true}");
        }
     }
   else if(SameOp(op, "subscribeTicks"))
     {
      string symbol = JsonString(line, "symbol");
      if(symbol == "")
         SendError(id, "subscribeTicks requires 'symbol'.");
      else
        {
         AddTickSubscription(symbol);
         SendOk(id, "{\"subscribed\":true}");
        }
     }
   else
      SendError(id, "Unknown op '" + op + "'.");
  }

void AddBarSubscription(string symbol, string interval)
  {
   for(int i = 0; i < ArraySize(gBarSymbols); i++)
      if(gBarSymbols[i] == symbol && gBarIntervals[i] == interval)
         return;

   int n = ArraySize(gBarSymbols);
   ArrayResize(gBarSymbols, n + 1);
   ArrayResize(gBarIntervals, n + 1);
   ArrayResize(gBarLastTimes, n + 1);
   gBarSymbols[n]   = symbol;
   gBarIntervals[n] = interval;
   gBarLastTimes[n] = 0;
  }

void AddTickSubscription(string symbol)
  {
   for(int i = 0; i < ArraySize(gTickSymbols); i++)
      if(gTickSymbols[i] == symbol)
         return;

   int n = ArraySize(gTickSymbols);
   ArrayResize(gTickSymbols, n + 1);
   gTickSymbols[n] = symbol;
  }

//+------------------------------------------------------------------+
//| Notifications                                                    |
//+------------------------------------------------------------------+
void PublishBars()
  {
   if(!gClientValid)
      return;

   MqlRates rates[];
   ArrayResize(rates, 1);

   for(int i = 0; i < ArraySize(gBarSymbols); i++)
     {
      string symbol = gBarSymbols[i];
      ENUM_TIMEFRAMES tf = Timeframe(gBarIntervals[i]);
      datetime current = iTime(symbol, tf, 0);
      if(current == 0 || current == gBarLastTimes[i])
         continue;

      gBarLastTimes[i] = current;
      if(CopyRates(symbol, tf, 0, 1, rates) != 1)
         continue;

      SendLine(BarNotificationJson(symbol, gBarIntervals[i], rates[0]));
     }
  }

void PublishTicks()
  {
   if(!gClientValid)
      return;

   for(int i = 0; i < ArraySize(gTickSymbols); i++)
     {
      if(SymbolInfoDouble(gTickSymbols[i], SYMBOL_BID) <= 0)
         continue;
      SendLine(TickNotificationJson(gTickSymbols[i]));
     }
  }

//+------------------------------------------------------------------+
//| EA events                                                        |
//+------------------------------------------------------------------+
void OnTick()
  {
   AcceptClient();
   ReadClient();
   PublishBars();
   PublishTicks();
  }

void OnTimer()
  {
   AcceptClient();
   ReadClient();
   PublishBars();
  }

void OnTradeTransaction(const MqlTradeTransaction &trans,
                        const MqlTradeRequest &request,
                        const MqlTradeResult &result)
  {
   if(!gClientValid)
      return;

   string status = "Submitted";
   switch(trans.type)
     {
      case TRADE_TRANSACTION_DEAL_ADD:    status = "Filled";     break;
      case TRADE_TRANSACTION_ORDER_ADD:   status = "New";        break;
      case TRADE_TRANSACTION_ORDER_UPDATE:status = "Submitted";  break;
      case TRADE_TRANSACTION_ORDER_DELETE:status = "Cancelled";  break;
      case TRADE_TRANSACTION_REQUEST:
         if((int)result.retcode == TRADE_RETCODE_DONE) status = "Filled";
         else if((int)result.retcode != TRADE_RETCODE_PLACED) status = "Rejected";
         break;
     }

   string brokerId = (string)trans.order;
   if(brokerId == "0")
      brokerId = (string)trans.deal;

   string message = (trans.type == TRADE_TRANSACTION_REQUEST && (int)result.retcode != TRADE_RETCODE_DONE &&
                     (int)result.retcode != TRADE_RETCODE_PLACED)
                    ? result.comment : "";

   SendLine(StringFormat("{\"op\":\"orderUpdate\",\"result\":%s}",
                         OrderResultJson(brokerId, request.comment, status,
                                         trans.volume, trans.price, message)));
  }
//+------------------------------------------------------------------+
