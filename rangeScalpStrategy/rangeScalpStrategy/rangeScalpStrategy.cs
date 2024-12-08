using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace rangeScalpStrategy
{
    public sealed class rangeScalpStrategy : Strategy, ICurrentAccount, ICurrentSymbol
    {
        [InputParameter("Symbol", 0)]
        public Symbol CurrentSymbol { get; set; }

        /// <summary>
        /// Account to place orders
        /// </summary>
        [InputParameter("Account", 1)]
        public Account CurrentAccount { get; set; }

        [InputParameter("Quantity")]
        public int Quantity = 1;

        /// <summary>
        /// Period to load history
        /// </summary>
        [InputParameter("Period", 5)]
        public Period Period { get; set; }

        /// <summary>
        /// Start point to load history
        /// </summary>
        [InputParameter("Start point", 6)]
        public DateTime StartPoint { get; set; }

        [InputParameter("Stop Loss")]
        public int stopLoss = 10;

        [InputParameter("Take Profit")]
        public int takeProfit = 5;

        [InputParameter("Max Trades")]
        public int maxTrades = 20;

        [InputParameter("Max Profit")]
        public int maxProfit = 350;

        [InputParameter("Max Loss")]
        public int maxLoss = 250;

        [InputParameter("Range Offset (in ticks)")]
        public int rangeOffsetTicks = 2;

        [InputParameter("Updates Before Initialization")]
        public int updateCounter = 1000;

        //[InputParameter("Short Range Addition")]
        //public bool shortRange = false;

        public override string[] MonitoringConnectionsIds => new string[] { this.CurrentSymbol?.ConnectionId, this.CurrentAccount?.ConnectionId };

        private HistoricalData hdm;

        private int longPositionsCount;
        private int shortPositionsCount;
        //private string orderTypeId;

        private bool waitOpenPosition;
        private bool waitClosePositions;

        private double totalNetPl;
        private double totalGrossPl;
        private double totalFee;

        private int tradeCounter = 0;
        //private bool inPosition = false;
        private bool buyPlaced = false;
        private bool sellPlaced = false;
        //private bool shortBuyPlaced = false;
        //private bool shortSellPlaced = false;
        private double price = 0.0;
        private double rangeHigh = 0.0;
        private double rangeLow = 999999.0;
        //private double shortRangeHigh = 0.0;
        //private double shortRangeLow = 999999.0;
        private int initCounter = 0;

        public rangeScalpStrategy()
            : base()
        {
            this.Name = "Quantavius_RS";
            this.Description = "Range Scalp Algo";

            this.Period = Period.SECOND30;
            this.StartPoint = Core.TimeUtils.DateTimeUtcNow.AddDays(-1);
        }

        protected override void OnRun()
        {
            this.totalNetPl = 0D;

            // Restore symbol object from active connection
            if (this.CurrentSymbol != null && this.CurrentSymbol.State == BusinessObjectState.Fake)
            {
                this.CurrentSymbol = Core.Instance.GetSymbol(this.CurrentSymbol.CreateInfo());
            }

            if (this.CurrentSymbol == null)
            {
                this.Log("Incorrect input parameters... Symbol have not specified.", StrategyLoggingLevel.Error);
                return;
            }

            // Restore account object from active connection
            if (this.CurrentAccount != null && this.CurrentAccount.State == BusinessObjectState.Fake)
            {
                this.CurrentAccount = Core.Instance.GetAccount(this.CurrentAccount.CreateInfo());
            }

            if (this.CurrentAccount == null)
            {
                this.Log("Incorrect input parameters... Account have not specified.", StrategyLoggingLevel.Error);
                return;
            }

            if (this.CurrentSymbol.ConnectionId != this.CurrentAccount.ConnectionId)
            {
                this.Log("Incorrect input parameters... Symbol and Account from different connections.", StrategyLoggingLevel.Error);
                return;
            }

            //this.orderTypeId = Core.OrderTypes.FirstOrDefault(x => x.ConnectionId == this.CurrentSymbol.ConnectionId && x.Behavior == OrderTypeBehavior.Market).Id;

            //if (string.IsNullOrEmpty(this.orderTypeId))
            //{
            //    this.Log("Connection of selected symbol has not support market orders", StrategyLoggingLevel.Error);
            //    return;
            //}

            this.hdm = this.CurrentSymbol.GetHistory(this.Period, this.CurrentSymbol.HistoryType, this.StartPoint);

            Core.PositionAdded += this.Core_PositionAdded;
            Core.PositionRemoved += this.Core_PositionRemoved;

            Core.OrdersHistoryAdded += this.Core_OrdersHistoryAdded;

            Core.TradeAdded += this.Core_TradeAdded;

            this.hdm.HistoryItemUpdated += this.Hdm_HistoryItemUpdated;
            this.hdm = this.CurrentSymbol.GetHistory(this.Period, this.CurrentSymbol.LastDateTime);
            this.hdm.NewHistoryItem += this.Hdm_OnNewHistoryItem;

        }

        protected override void OnStop()
        {
            Core.PositionAdded -= this.Core_PositionAdded;
            Core.PositionRemoved -= this.Core_PositionRemoved;

            Core.OrdersHistoryAdded -= this.Core_OrdersHistoryAdded;

            Core.TradeAdded -= this.Core_TradeAdded;

            if (this.hdm != null)
            {
                this.hdm.HistoryItemUpdated -= this.Hdm_HistoryItemUpdated;
                this.hdm.Dispose();
            }

            base.OnStop();
        }

        protected override void OnInitializeMetrics(Meter meter)
        {
            base.OnInitializeMetrics(meter);

            meter.CreateObservableCounter("total-long-positions", () => this.longPositionsCount, description: "Total long positions");
            meter.CreateObservableCounter("total-short-positions", () => this.shortPositionsCount, description: "Total short positions");

            meter.CreateObservableCounter("total-pl-net", () => this.totalNetPl, description: "Total Net profit/loss");
            meter.CreateObservableCounter("total-pl-gross", () => this.totalGrossPl, description: "Total Gross profit/loss");
            meter.CreateObservableCounter("total-fee", () => this.totalFee, description: "Total fee");
            meter.CreateObservableCounter("trade-count", () => this.tradeCounter, description: "Trade Count");
        }

        private void Core_PositionAdded(Position obj)
        {
            var positions = Core.Instance.Positions.Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount).ToArray();

            this.longPositionsCount = positions.Count(x => x.Side == Side.Buy);
            this.shortPositionsCount = positions.Count(x => x.Side == Side.Sell);

            double currentPositionsQty = positions.Sum(x => x.Side == Side.Buy ? x.Quantity : -x.Quantity);

            if (Math.Abs(currentPositionsQty) == this.Quantity)
            {
                this.waitOpenPosition = false;
            }
            //this.inPosition = true;
            this.tradeCounter += 1;
        }

        private void Core_PositionRemoved(Position obj)
        {
            var positions = Core.Instance.Positions.Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount).ToArray();
            var orders = Core.Instance.Orders.Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount).ToArray();
            this.longPositionsCount = positions.Count(x => x.Side == Side.Buy);
            this.shortPositionsCount = positions.Count(x => x.Side == Side.Sell);

            if (positions.Length == 0)
            {
                this.waitClosePositions = false;
                //this.inPosition = false;
                this.sellPlaced = false;
                this.buyPlaced = false;
                //this.shortSellPlaced = false;
                //this.shortBuyPlaced = false;
                foreach (var items in orders)
                {
                    var result = items.Cancel();
                }
            }
        }

        private void Core_OrdersHistoryAdded(OrderHistory obj)
        {
            if (obj.Symbol == this.CurrentSymbol)
            {
                return;
            }

            if (obj.Account == this.CurrentAccount)
            {
                return;
            }

            if (obj.Status == OrderStatus.Refused)
            {
                this.ProcessTradingRefuse();
            }
        }

        private void Core_TradeAdded(Trade obj)
        {
            if (obj.NetPnl != null)
            {
                this.totalNetPl += obj.NetPnl.Value;
            }

            if (obj.GrossPnl != null)
            {
                this.totalGrossPl += obj.GrossPnl.Value;
            }

            if (obj.Fee != null)
            {
                this.totalFee += obj.Fee.Value;
            }
        }

        private void Hdm_OnNewHistoryItem(object sender, HistoryEventArgs args)
        {
            //this.Log("New Bar");
            //this.newBar = true;
            if (this.initCounter <= this.updateCounter)
            {
                return;
            }
            if (this.tradeCounter >= this.maxTrades)
            {
                return;
            }
            if (this.totalGrossPl >= this.maxProfit || this.totalGrossPl <= -this.maxLoss)
            {
                return;
            }
            double close_1 = HistoricalDataExtensions.Close(this.hdm, 1);
            double open_1 = HistoricalDataExtensions.Open(this.hdm, 1);
            if (close_1 > open_1 && this.sellPlaced == false) // Green bar
            {
                this.waitOpenPosition = true;
                this.Log("Start open sell position");
                var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters()
                {
                    Account = this.CurrentAccount,
                    Symbol = this.CurrentSymbol,
                    TakeProfit = SlTpHolder.CreateTP(this.takeProfit, PriceMeasurement.Offset),
                    StopLoss = SlTpHolder.CreateSL(this.stopLoss, PriceMeasurement.Offset),
                    TriggerPrice = this.rangeLow - (this.rangeOffsetTicks * .25),
                    OrderTypeId = OrderType.Stop,
                    Quantity = this.Quantity,
                    Side = Side.Sell,
                });

                if (result.Status == TradingOperationResultStatus.Failure)
                {
                    this.Log($"Place sell order refuse: {(string.IsNullOrEmpty(result.Message) ? result.Status : result.Message)}", StrategyLoggingLevel.Trading);
                    this.ProcessTradingRefuse();
                }
                else
                {
                    this.Log($"Position open: {result.Status}", StrategyLoggingLevel.Trading);
                    //this.inPosition = true;
                    this.sellPlaced = true;
                }
            }
            else if (close_1 < open_1 && this.buyPlaced == false) // Red bar
            {
                this.waitOpenPosition = true;
                this.Log("Start open buy position");
                var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters()
                {
                    Account = this.CurrentAccount,
                    Symbol = this.CurrentSymbol,
                    TakeProfit = SlTpHolder.CreateTP(this.takeProfit, PriceMeasurement.Offset),
                    StopLoss = SlTpHolder.CreateSL(this.stopLoss, PriceMeasurement.Offset),
                    TriggerPrice = this.rangeHigh + (this.rangeOffsetTicks * .25),
                    OrderTypeId = OrderType.Stop,
                    Quantity = this.Quantity,
                    Side = Side.Buy,
                });

                if (result.Status == TradingOperationResultStatus.Failure)
                {
                    this.Log($"Place buy order refuse: {(string.IsNullOrEmpty(result.Message) ? result.Status : result.Message)}", StrategyLoggingLevel.Trading);
                    this.ProcessTradingRefuse();
                }
                else
                {
                    this.Log($"Position open: {result.Status}", StrategyLoggingLevel.Trading);
                    //this.inPosition = true;
                    this.buyPlaced = true;
                }
            }
            //if (this.shortRange == true)
            //{
            //    if (close_1 > open_1 && this.shortSellPlaced == false && this.shortRangeLow != this.rangeLow) // Green bar
            //    {
            //        this.waitOpenPosition = true;
            //        this.Log("Start open sell position");
            //        var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters()
            //        {
            //            Account = this.CurrentAccount,
            //            Symbol = this.CurrentSymbol,
            //            TakeProfit = SlTpHolder.CreateTP(this.takeProfit, PriceMeasurement.Offset),
            //            StopLoss = SlTpHolder.CreateSL(this.stopLoss, PriceMeasurement.Offset),
            //            TriggerPrice = this.shortRangeLow - (this.rangeOffsetTicks * .25),
            //            OrderTypeId = OrderType.Stop,
            //            Quantity = this.Quantity,
            //            Side = Side.Sell,
            //        });

            //        if (result.Status == TradingOperationResultStatus.Failure)
            //        {
            //            this.Log($"Place sell order refuse: {(string.IsNullOrEmpty(result.Message) ? result.Status : result.Message)}", StrategyLoggingLevel.Trading);
            //            this.ProcessTradingRefuse();
            //        }
            //        else
            //        {
            //            this.Log($"Position open: {result.Status}", StrategyLoggingLevel.Trading);
            //            //this.inPosition = true;
            //            this.shortSellPlaced = true;
            //        }
            //    }
            //    else if (close_1 < open_1 && this.shortBuyPlaced == false && this.shortRangeHigh != this.rangeHigh) // Red bar
            //    {
            //        this.waitOpenPosition = true;
            //        this.Log("Start open buy position");
            //        var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters()
            //        {
            //            Account = this.CurrentAccount,
            //            Symbol = this.CurrentSymbol,
            //            TakeProfit = SlTpHolder.CreateTP(this.takeProfit, PriceMeasurement.Offset),
            //            StopLoss = SlTpHolder.CreateSL(this.stopLoss, PriceMeasurement.Offset),
            //            TriggerPrice = this.shortRangeHigh + (this.rangeOffsetTicks * .25),
            //            OrderTypeId = OrderType.Stop,
            //            Quantity = this.Quantity,
            //            Side = Side.Buy,
            //        });

            //        if (result.Status == TradingOperationResultStatus.Failure)
            //        {
            //            this.Log($"Place buy order refuse: {(string.IsNullOrEmpty(result.Message) ? result.Status : result.Message)}", StrategyLoggingLevel.Trading);
            //            this.ProcessTradingRefuse();
            //        }
            //        else
            //        {
            //            this.Log($"Position open: {result.Status}", StrategyLoggingLevel.Trading);
            //            //this.inPosition = true;
            //            this.shortBuyPlaced = true;
            //        }
            //    }
            //}
        }

        private void Hdm_HistoryItemUpdated(object sender, HistoryEventArgs e) => this.OnUpdate();

        private void OnUpdate()
        {
            if (this.initCounter < this.updateCounter + 5)
            {
                this.initCounter++;
            }

            double price = HistoricalDataExtensions.Close(this.hdm, 0);

            if (price >= this.rangeHigh)
            {
                this.rangeHigh = price;
            }
            if (price <= this.rangeLow)
            {
                this.rangeLow = price;
            }

            //if (this.shortRange == true)
            //{
            //    double high_0 = HistoricalDataExtensions.High(this.hdm, 0);
            //    double low_0 = HistoricalDataExtensions.Low(this.hdm, 0);
            //    double high_1 = HistoricalDataExtensions.High(this.hdm, 1);
            //    double low_1 = HistoricalDataExtensions.Low(this.hdm, 1);
            //    double high_2 = HistoricalDataExtensions.High(this.hdm, 2);
            //    double low_2 = HistoricalDataExtensions.Low(this.hdm, 2);
            //    double high_3 = HistoricalDataExtensions.High(this.hdm, 3);
            //    double low_3 = HistoricalDataExtensions.Low(this.hdm, 3);
            //    double high_4 = HistoricalDataExtensions.High(this.hdm, 4);
            //    double low_4 = HistoricalDataExtensions.Low(this.hdm, 4);
            //    double high_5 = HistoricalDataExtensions.High(this.hdm, 5);
            //    double low_5 = HistoricalDataExtensions.Low(this.hdm, 5);
            //    double high_6 = HistoricalDataExtensions.High(this.hdm, 6);
            //    double low_6 = HistoricalDataExtensions.Low(this.hdm, 6);
            //    double high_7 = HistoricalDataExtensions.High(this.hdm, 7);
            //    double low_7 = HistoricalDataExtensions.Low(this.hdm, 7);
            //    double high_8 = HistoricalDataExtensions.High(this.hdm, 8);
            //    double low_8 = HistoricalDataExtensions.Low(this.hdm, 8);
            //    double high_9 = HistoricalDataExtensions.High(this.hdm, 9);
            //    double low_9 = HistoricalDataExtensions.Low(this.hdm, 9);
            //    double high_10 = HistoricalDataExtensions.High(this.hdm, 10);
            //    double low_10 = HistoricalDataExtensions.Low(this.hdm, 10);

            //    double[] shortRanges_10 = [high_0, low_0, high_1, low_1, high_2, low_2, high_3, low_3, high_4, low_4, high_5, low_5, high_6, low_6, high_7, low_7, high_8, low_8, high_9, low_9, high_10, low_10];
            //    //double[] shortRanges_5 = [high_0, low_0, high_1, low_1, high_2, low_2, high_3, low_3, high_4, low_4, high_5, low_5];

            //    for (int i = 0; i < shortRanges_10.Length; i++)
            //    {
            //        if (shortRanges_10[i] >= this.shortRangeHigh)
            //        {
            //            this.shortRangeHigh = shortRanges_10[i];
            //        }
            //        if (shortRanges_10[i] <= this.shortRangeLow)
            //        {
            //            this.shortRangeLow = shortRanges_10[i];
            //        }
            //    }
            //}
        }

        private void ProcessTradingRefuse()
        {
            this.Log("Strategy have received refuse for trading action. It should be stopped", StrategyLoggingLevel.Error);
            this.Stop();
        }
    }
}