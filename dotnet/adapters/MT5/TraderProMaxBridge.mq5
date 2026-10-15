#property strict
#property version "2.00"
#property description "Dedicated Exness DEMO adapter for TraderProMax C#; no strategy/file polling"
#include "MiniJson.mqh"
input string BridgeToken="";
input int BridgePort=8901;
input long ExpectedLogin=0;
input string ExpectedServer="";
input string TradingSymbol="US500m";
input long StrategyMagic=26101401;
input bool AllowDemoOrders=false;

int link=INVALID_HANDLE;uchar received[];bool accepted=false;ulong last_connect=0,last_account=0,last_tick=0;
double last_bid=0,last_ask=0;
struct Pending {string command,cycle,side,action;uint request;ulong order,position,identifier;bool retired;double entryVolume,exitVolume;};
Pending pending[1024];int pending_count=0;string seen[4096];int seen_at=0;
const string NIL="00000000-0000-0000-0000-000000000000";
bool DemoIdentity(){return AccountInfoInteger(ACCOUNT_TRADE_MODE)==ACCOUNT_TRADE_MODE_DEMO&&AccountInfoInteger(ACCOUNT_MARGIN_MODE)==ACCOUNT_MARGIN_MODE_RETAIL_HEDGING&&AccountInfoString(ACCOUNT_CURRENCY)=="USD"&&StringFind(AccountInfoString(ACCOUNT_SERVER),"Exness")>=0&&ExpectedLogin>0&&AccountInfoInteger(ACCOUNT_LOGIN)==ExpectedLogin&&AccountInfoString(ACCOUNT_SERVER)==ExpectedServer;}
void I32(uchar &b[],int p,uint v){for(int i=0;i<4;i++)b[p+i]=(uchar)(v>>(8*i));}
uint R32(const uchar &b[],int p){uint v=0;for(int i=0;i<4;i++)v|=((uint)b[p+i])<<(8*i);return v;}
string Num(double v){return DoubleToString(v,10);}
string Integer(long v){return (string)v;}
bool Send(int type,string json)
{
   if(link==INVALID_HANDLE)return false;uchar data[];int n=StringToCharArray(json,data,0,WHOLE_ARRAY,CP_UTF8)-1;if(n<0||n>65536)return false;
   uchar frame[];ArrayResize(frame,n+12);I32(frame,0,(uint)n);I32(frame,4,0x32504D54);frame[8]=1;frame[9]=0;frame[10]=(uchar)type;frame[11]=0;ArrayCopy(frame,data,12,0,n);
   uint sent=0;while(sent<(uint)ArraySize(frame)){uchar part[];ArrayCopy(part,frame,0,(int)sent);int wrote=SocketSend(link,part,ArraySize(part));if(wrote<=0)return false;sent+=(uint)wrote;}
   return true;
}
void Disconnect(){if(link!=INVALID_HANDLE)SocketClose(link);link=INVALID_HANDLE;accepted=false;last_tick=0;last_account=0;last_bid=0;last_ask=0;ArrayResize(received,0);}
bool Connect()
{
   if(BridgeToken==""||BridgePort<1024||!DemoIdentity())return false;
   link=SocketCreate();if(link==INVALID_HANDLE)return false;
   if(!SocketConnect(link,"127.0.0.1",(uint)BridgePort,200)){Disconnect();return false;}SocketTimeouts(link,10,10);
   string h="{\"token\":"+JsonString(BridgeToken)+",\"login\":"+Integer(ExpectedLogin)+",\"server\":"+JsonString(ExpectedServer)+",\"currency\":\"USD\",\"demo\":true,\"hedging\":true,\"symbol\":"+JsonString(TradingSymbol)+",\"contractSize\":"+Num(SymbolInfoDouble(TradingSymbol,SYMBOL_TRADE_CONTRACT_SIZE))+",\"tickSize\":"+Num(SymbolInfoDouble(TradingSymbol,SYMBOL_TRADE_TICK_SIZE))+",\"minLot\":"+Num(SymbolInfoDouble(TradingSymbol,SYMBOL_VOLUME_MIN))+",\"lotStep\":"+Num(SymbolInfoDouble(TradingSymbol,SYMBOL_VOLUME_STEP))+",\"magic\":"+Integer(StrategyMagic)+",\"stopDistance\":"+Num(SymbolInfoInteger(TradingSymbol,SYMBOL_TRADE_STOPS_LEVEL)*SymbolInfoDouble(TradingSymbol,SYMBOL_POINT))+"}";
   h=StringSubstr(h,0,StringLen(h)-1)+",\"terminalPath\":"+JsonString(TerminalInfoString(TERMINAL_PATH)+"\\terminal64.exe")+",\"ordersAllowed\":"+(AllowDemoOrders?"true":"false")+"}";
   if(!Send(1,h)){Disconnect();return false;}return true;
}
int IndexCommand(string id){for(int i=0;i<pending_count;i++)if(pending[i].command==id)return i;return -1;}
int IndexPosition(ulong position,ulong identifier=0){for(int i=0;i<pending_count;i++)if(!pending[i].retired&&pending[i].action=="open"&&((pending[i].position==position&&position>0)||(pending[i].identifier==identifier&&identifier>0)))return i;return -1;}
int Reserve(){for(int i=0;i<pending_count;i++)if(pending[i].retired){Pending empty;pending[i]=empty;return i;}if(pending_count>=1024)return -1;return pending_count++;}
bool Duplicate(string id){for(int i=0;i<4096;i++)if(seen[i]==id)return true;seen[seen_at++%4096]=id;return false;}
string Tag(string cycle){StringReplace(cycle,"-","");return "TP"+StringSubstr(cycle,0,26);}
void Report(string kind,int index,int code,string why,ulong deal=0,ulong order=0,ulong position=0,double volume=0,double price=0,double sl=0,double tp=0,double pnl=0,double commission=0,double swap=0,double fee=0,long ns=0,ulong identifier=0)
{
   string cmd=index>=0?pending[index].command:NIL,cycle=index>=0?pending[index].cycle:NIL,side=index>=0?pending[index].side:"";
   string row="{\"kind\":"+JsonString(kind)+",\"commandId\":"+JsonString(cmd)+",\"cycleId\":"+JsonString(cycle)+",\"requestId\":"+Integer(index>=0?(long)pending[index].request:0)+",\"order\":"+Integer((long)order)+",\"deal\":"+Integer((long)deal)+",\"position\":"+Integer((long)position)+",\"identifier\":"+Integer((long)identifier)+",\"side\":"+JsonString(side)+",\"volume\":"+Num(volume)+",\"price\":"+Num(price)+",\"sl\":"+Num(sl)+",\"tp\":"+Num(tp)+",\"pnl\":"+Num(pnl)+",\"commission\":"+Num(commission)+",\"swap\":"+Num(swap)+",\"fee\":"+Num(fee)+",\"timeNs\":"+Integer(ns)+",\"retcode\":"+Integer(code)+",\"reason\":"+JsonString(why)+"}";Send(8,row);
}
void Reconcile(int index,string json)
{
   pending[index].command=JString(json,"referenceCommandId");pending[index].action="open";
   datetime from=(datetime)(JLong(json,"fromNs")/1000000000-10);
   if(!HistorySelect(from,TimeCurrent()+86400)){Report("leg_reconciled",index,10012,"History selection failed");pending[index].retired=true;return;}
   ulong identifier=(ulong)JLong(json,"identifier");int count=HistoryDealsTotal();
   if(count>20000){Report("leg_reconciled",index,10012,"History too large; request explicit reconciliation");pending[index].retired=true;return;}
   for(int d=0;d<count&&identifier==0;d++)
   {
      ulong ticket=HistoryDealGetTicket(d);
      if(HistoryDealGetInteger(ticket,DEAL_MAGIC)!=StrategyMagic||HistoryDealGetString(ticket,DEAL_SYMBOL)!=TradingSymbol||HistoryDealGetInteger(ticket,DEAL_ENTRY)!=DEAL_ENTRY_IN||HistoryDealGetString(ticket,DEAL_COMMENT)!=Tag(pending[index].cycle))continue;
      long type=HistoryDealGetInteger(ticket,DEAL_TYPE);if((pending[index].side=="LONG"&&type!=DEAL_TYPE_BUY)||(pending[index].side=="SHORT"&&type!=DEAL_TYPE_SELL))continue;
      identifier=(ulong)HistoryDealGetInteger(ticket,DEAL_POSITION_ID);
   }
   pending[index].identifier=identifier;
   ulong position=0;double remaining=0,sl=0,tp=0;
   for(int p=0;p<PositionsTotal();p++)
   {
      ulong ticket=PositionGetTicket(p);if(PositionGetInteger(POSITION_MAGIC)!=StrategyMagic||PositionGetString(POSITION_SYMBOL)!=TradingSymbol)continue;
      if(identifier>0&&(ulong)PositionGetInteger(POSITION_IDENTIFIER)==identifier){position=ticket;remaining=PositionGetDouble(POSITION_VOLUME);sl=PositionGetDouble(POSITION_SL);tp=PositionGetDouble(POSITION_TP);break;}
   }
   pending[index].position=position;
   bool outstanding=false;
   for(int o=0;o<OrdersTotal();o++){OrderGetTicket(o);if(OrderGetInteger(ORDER_MAGIC)==StrategyMagic&&OrderGetString(ORDER_COMMENT)==Tag(pending[index].cycle))outstanding=true;}
   for(int d=0;d<count&&identifier>0;d++)
   {
      ulong ticket=HistoryDealGetTicket(d);if((ulong)HistoryDealGetInteger(ticket,DEAL_POSITION_ID)!=identifier)continue;
      long entry=HistoryDealGetInteger(ticket,DEAL_ENTRY);if(entry!=DEAL_ENTRY_IN&&entry!=DEAL_ENTRY_OUT&&entry!=DEAL_ENTRY_OUT_BY){outstanding=true;continue;}
      double volume=HistoryDealGetDouble(ticket,DEAL_VOLUME);if(entry==DEAL_ENTRY_IN)pending[index].entryVolume+=volume;else pending[index].exitVolume+=volume;
      Report(entry==DEAL_ENTRY_IN?"history_fill":"history_close",index,10009,"Broker history",ticket,(ulong)HistoryDealGetInteger(ticket,DEAL_ORDER),position,volume,HistoryDealGetDouble(ticket,DEAL_PRICE),0,0,HistoryDealGetDouble(ticket,DEAL_PROFIT),HistoryDealGetDouble(ticket,DEAL_COMMISSION),HistoryDealGetDouble(ticket,DEAL_SWAP),HistoryDealGetDouble(ticket,DEAL_FEE),HistoryDealGetInteger(ticket,DEAL_TIME_MSC)*1000000,identifier);
   }
   Report("leg_reconciled",index,outstanding?10012:10009,outstanding?"Order/history still uncertain":"Complete selected position/deal snapshot",0,0,position,remaining,0,sl,tp,0,0,0,0,(long)TimeGMT()*1000000000,identifier);
   if(remaining==0)pending[index].retired=true;
}
ENUM_ORDER_TYPE_FILLING Filling()
{
   long mode=SymbolInfoInteger(TradingSymbol,SYMBOL_FILLING_MODE);
   if((mode&SYMBOL_FILLING_FOK)!=0)return ORDER_FILLING_FOK;
   if((mode&SYMBOL_FILLING_IOC)!=0)return ORDER_FILLING_IOC;
   return ORDER_FILLING_RETURN;
}
double RoundPrice(double price){double tick=SymbolInfoDouble(TradingSymbol,SYMBOL_TRADE_TICK_SIZE);return NormalizeDouble(MathRound(price/tick)*tick,(int)SymbolInfoInteger(TradingSymbol,SYMBOL_DIGITS));}
void Command(string json)
{
   string id=JString(json,"id");if(id==""||IndexCommand(id)>=0||Duplicate(id))return;
   string action=JString(json,"action");
   int i=action=="reconcile"?IndexCommand(JString(json,"referenceCommandId")):-1;
   if(i<0)i=Reserve();if(i<0){Disconnect();return;}Pending empty;pending[i]=empty;pending[i].command=id;pending[i].cycle=JString(json,"cycleId");pending[i].side=JString(json,"side");pending[i].position=(ulong)JLong(json,"position");
   pending[i].action=action;
   if(action=="reconcile"&&DemoIdentity()&&accepted&&JString(json,"symbol")==TradingSymbol&&JLong(json,"magic")==StrategyMagic){Reconcile(i,json);return;}
   if(!AllowDemoOrders||!DemoIdentity()||!accepted||JString(json,"symbol")!=TradingSymbol||JLong(json,"magic")!=StrategyMagic){Report("rejected",i,10017,"DEMO orders not armed or identity mismatch");return;}
   if(action=="open"&&(JLong(json,"deadlineBootMs")<(long)GetTickCount64()||MathAbs(JDouble(json,"risk")-20)>1e-8||MathAbs(JDouble(json,"targetR")-1.16)>1e-8||MathAbs(JDouble(json,"lots")-.2)>1e-8)){Report("rejected",i,10013,"Expired or altered registered entry");return;}
   MqlTradeRequest req={};MqlTradeResult result={};MqlTradeCheckResult check={};MqlTick q;if(!SymbolInfoTick(TradingSymbol,q)||q.ask<=q.bid){Report("rejected",i,10021,"Executable quote unavailable");return;}
   req.symbol=TradingSymbol;req.magic=(ulong)StrategyMagic;req.comment=Tag(pending[i].cycle);req.type_filling=Filling();req.deviation=50;
   if(action=="open")
   {
      req.action=TRADE_ACTION_DEAL;req.type=pending[i].side=="LONG"?ORDER_TYPE_BUY:ORDER_TYPE_SELL;req.volume=JDouble(json,"lots");req.price=req.type==ORDER_TYPE_BUY?q.ask:q.bid;
      double sign=req.type==ORDER_TYPE_BUY?1:-1,risk=JDouble(json,"risk");req.sl=RoundPrice(req.price-sign*risk);req.tp=RoundPrice(req.price+sign*risk*JDouble(json,"targetR"));
      for(int p=0;p<PositionsTotal();p++){PositionGetTicket(p);if(PositionGetInteger(POSITION_MAGIC)!=StrategyMagic||PositionGetString(POSITION_SYMBOL)!=TradingSymbol){Report("rejected",i,10013,"Foreign inventory on dedicated account");pending[i].retired=true;return;}}
      if(OrdersTotal()>0){Report("rejected",i,10013,"Outstanding orders require reconciliation");pending[i].retired=true;return;}
      double margin,other=0;
      if(!OrderCalcMargin(req.type,req.symbol,req.volume,req.price,margin)){Report("rejected",i,10019,"Margin calculation unavailable");return;}
      if(JLong(json,"reserveLegs")==2&&!OrderCalcMargin(req.type==ORDER_TYPE_BUY?ORDER_TYPE_SELL:ORDER_TYPE_BUY,req.symbol,req.volume,req.type==ORDER_TYPE_BUY?q.bid:q.ask,other)){Report("rejected",i,10019,"Both-leg margin calculation unavailable");return;}
      if(AccountInfoDouble(ACCOUNT_MARGIN_FREE)<margin+other+5){Report("rejected",i,10019,"Both-leg margin plus reserve unavailable");pending[i].retired=true;return;}
   }
   else if(action=="protect"||action=="close")
   {
      if(!PositionSelectByTicket(pending[i].position)||PositionGetInteger(POSITION_MAGIC)!=StrategyMagic||PositionGetString(POSITION_SYMBOL)!=TradingSymbol||PositionGetString(POSITION_COMMENT)!=Tag(pending[i].cycle)){Report("uncertain",i,10036,"Owned position not found");return;}
      req.position=pending[i].position;
      if(action=="protect"){req.action=TRADE_ACTION_SLTP;req.sl=JDouble(json,"sl");req.tp=JDouble(json,"tp");}
      else{req.action=TRADE_ACTION_DEAL;req.volume=PositionGetDouble(POSITION_VOLUME);req.type=PositionGetInteger(POSITION_TYPE)==POSITION_TYPE_BUY?ORDER_TYPE_SELL:ORDER_TYPE_BUY;req.price=req.type==ORDER_TYPE_BUY?q.ask:q.bid;}
   }
   else{Report("rejected",i,10013,"Unknown native command");return;}
   if(!OrderCheck(req,check)||(check.retcode!=0&&check.retcode!=10009)){Report("rejected",i,(int)check.retcode,check.comment);return;}
   if(!OrderSendAsync(req,result)){Report("uncertain",i,(int)result.retcode,result.comment);return;}
   pending[i].request=result.request_id;pending[i].order=result.order;Report("submitted",i,(int)result.retcode,"Request sent; fill not yet confirmed");
}
void Drain()
{
   if(link==INVALID_HANDLE)return;uint n=SocketIsReadable(link);if(n>0)
   {
      uchar part[];int count=SocketRead(link,part,MathMin(n,65536),1);if(count<=0){Disconnect();return;}
      int old=ArraySize(received);if(old+count>131072){Disconnect();return;}ArrayResize(received,old+count);ArrayCopy(received,part,old,0,count);
   }
   int processed=0;
   while(ArraySize(received)>=12&&processed++<64)
   {
      uint length=R32(received,0);if(length>65536||R32(received,4)!=0x32504D54||received[8]!=1||received[9]!=0){Disconnect();return;}
      if(ArraySize(received)<12+(int)length)break;
      int type=(int)received[10]+((int)received[11]<<8);string json=CharArrayToString(received,12,(int)length,CP_UTF8);
      int remain=ArraySize(received)-12-(int)length;uchar rest[];ArrayCopy(rest,received,0,12+(int)length,remain);ArrayResize(received,remain);if(remain>0)ArrayCopy(received,rest);
      if(type==7)accepted=JString(json,"accepted")=="true";
      else if(type==9)Command(json);
   }
}
void SendQuotes()
{
   if(!accepted)return;MqlTick q;if(!SymbolInfoTick(TradingSymbol,q))return;
   if((ulong)q.time_msc!=last_tick||q.bid!=last_bid||q.ask!=last_ask){last_tick=(ulong)q.time_msc;last_bid=q.bid;last_ask=q.ask;Send(8,"{\"kind\":\"quote\",\"bid\":"+Num(q.bid)+",\"ask\":"+Num(q.ask)+",\"sourceNs\":"+Integer(q.time_msc*1000000)+",\"receivedNs\":0}");}
   if(GetTickCount64()-last_account>=1000)
   {last_account=GetTickCount64();int foreign=0;for(int p=0;p<PositionsTotal();p++){PositionGetTicket(p);if(PositionGetInteger(POSITION_MAGIC)!=StrategyMagic||PositionGetString(POSITION_SYMBOL)!=TradingSymbol)foreign++;}Send(8,"{\"kind\":\"account\",\"login\":"+Integer(ExpectedLogin)+",\"server\":"+JsonString(ExpectedServer)+",\"balance\":"+Num(AccountInfoDouble(ACCOUNT_BALANCE))+",\"equity\":"+Num(AccountInfoDouble(ACCOUNT_EQUITY))+",\"freeMargin\":"+Num(AccountInfoDouble(ACCOUNT_MARGIN_FREE))+",\"margin\":"+Num(AccountInfoDouble(ACCOUNT_MARGIN))+",\"connected\":"+(TerminalInfoInteger(TERMINAL_CONNECTED)?"true":"false")+",\"positions\":"+Integer(PositionsTotal())+",\"orders\":"+Integer(OrdersTotal())+",\"foreignPositions\":"+Integer(foreign)+",\"timeNs\":0}");}
}
void OnTimer()
{
   if(!DemoIdentity()){Disconnect();return;}
   if(link==INVALID_HANDLE){if(GetTickCount64()-last_connect>=1000){last_connect=GetTickCount64();Connect();}return;}
   if(!SocketIsConnected(link)){Disconnect();return;}Drain();SendQuotes();
}
void OnTick(){Drain();SendQuotes();}
int OnInit()
{
   if(!DemoIdentity()||!SymbolSelect(TradingSymbol,true)){Print("TraderProMax: set the dedicated Exness DEMO identity and symbol first");return INIT_PARAMETERS_INCORRECT;}
   if(SymbolInfoDouble(TradingSymbol,SYMBOL_TRADE_CONTRACT_SIZE)!=1)return INIT_PARAMETERS_INCORRECT;
   EventSetMillisecondTimer(10);Print("TraderProMax C# bridge: permit 127.0.0.1 in MT5 allowed network addresses; DEMO only");return INIT_SUCCEEDED;
}
void OnDeinit(const int reason){EventKillTimer();Disconnect();}
void OnTradeTransaction(const MqlTradeTransaction &trans,const MqlTradeRequest &request,const MqlTradeResult &result)
{
   if(!DemoIdentity())return;
   if(trans.type==TRADE_TRANSACTION_REQUEST)
   {
      for(int i=0;i<pending_count;i++)if(pending[i].request==result.request_id)
      {
         pending[i].order=result.order;
         if(result.retcode!=10009&&result.retcode!=10008&&result.retcode!=10010){Report(result.retcode==10012||result.retcode==10031?"uncertain":"rejected",i,(int)result.retcode,result.comment);pending[i].retired=true;}
         else if(request.action==TRADE_ACTION_SLTP&&PositionSelectByTicket(request.position)){Report("protected",i,(int)result.retcode,"Server protection confirmed",0,0,request.position,0,0,PositionGetDouble(POSITION_SL),PositionGetDouble(POSITION_TP));pending[i].retired=true;}
         break;
      }
   }
   if(trans.type==TRADE_TRANSACTION_DEAL_ADD&&HistoryDealSelect(trans.deal)&&HistoryDealGetInteger(trans.deal,DEAL_MAGIC)==StrategyMagic)
   {
      ulong position=trans.position,order=(ulong)HistoryDealGetInteger(trans.deal,DEAL_ORDER),identifier=(ulong)HistoryDealGetInteger(trans.deal,DEAL_POSITION_ID);int index=IndexPosition(position,identifier);
      if(index<0)for(int i=0;i<pending_count;i++)if(pending[i].order==order||Tag(pending[i].cycle)==HistoryDealGetString(trans.deal,DEAL_COMMENT)){index=i;break;}
      if(index<0)return;pending[index].position=position;pending[index].identifier=identifier;
      bool entry=HistoryDealGetInteger(trans.deal,DEAL_ENTRY)==DEAL_ENTRY_IN;
      double volume=HistoryDealGetDouble(trans.deal,DEAL_VOLUME);if(entry)pending[index].entryVolume+=volume;else pending[index].exitVolume+=volume;
      Report(entry?"fill":"close",index,10009,"Broker deal",trans.deal,order,position,volume,HistoryDealGetDouble(trans.deal,DEAL_PRICE),0,0,HistoryDealGetDouble(trans.deal,DEAL_PROFIT),HistoryDealGetDouble(trans.deal,DEAL_COMMISSION),HistoryDealGetDouble(trans.deal,DEAL_SWAP),HistoryDealGetDouble(trans.deal,DEAL_FEE),HistoryDealGetInteger(trans.deal,DEAL_TIME_MSC)*1000000,identifier);
      if(!entry&&pending[index].exitVolume>=pending[index].entryVolume-0.000001)pending[index].retired=true;
   }
}
