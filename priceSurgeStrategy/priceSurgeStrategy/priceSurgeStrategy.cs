using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using TradingPlatform.BusinessLayer;

namespace PriceSurge
{
    public sealed class PriceSurge : Strategy, ICurrentAccount, ICurrentSymbol
    {
        [InputParameter("Symbol", 0)]
        public Symbol CurrentSymbol { get; set; }

        /// <summary>
        /// Account to place orders
        /// </summary>
        [InputParameter("Account", 1)]
        public Account CurrentAccount { get; set; }

        /// <summary>
        /// Period for Fast MA indicator
        /// </summary>
        //[InputParameter("Fast MA", 2, minimum: 1, maximum: 100, increment: 1, decimalPlaces: 0)]
        //public int FastMA { get; set; }

        /// <summary>
        /// Period for Slow MA indicator
        /// </summary>
        //[InputParameter("Slow MA", 3, minimum: 1, maximum: 100, increment: 1, decimalPlaces: 0)]
        //public int SlowMA { get; set; }

        /// <summary>
        /// Quantity to open order
        /// </summary>
        //[InputParameter("Quantity", 4, 0.1, 99999, 0.1, 2)]
        //public double Quantity { get; set; }

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

        [InputParameter("Multiplicative")]
        public double multiplicative = 1.15;

        [InputParameter("Trailing Stoploss")]
        public int trailingStop = 20;

        [InputParameter("Lookback Range (<= 10)")]
        public int lookbackRange = 10;

        public override string[] MonitoringConnectionsIds => new string[] { this.CurrentSymbol?.ConnectionId, this.CurrentAccount?.ConnectionId };

        //private Indicator indicatorFastMA;
        //private Indicator indicatorSlowMA;
        //private Indicator indicatorClass;
        //private Position currentPosition;
        private HistoricalData hdm;

        private int longPositionsCount;
        private int shortPositionsCount;
        private string orderTypeId;

        private bool waitOpenPosition;
        private bool waitClosePositions;

        private bool inPosition = false;
        private string prevSide = "none";
        private bool newBar = true;

        private double totalNetPl;
        private double totalGrossPl;
        private double totalFee;

        private string stopOrderId = default; // Added from code to cancel order and get id

        public PriceSurge()
            : base()
        {
            this.Name = "Quantavius_PS";
            this.Description = "Raw strategy without any additional function";

            //this.FastMA = 10;
            //this.SlowMA = 20;
            this.Period = Period.SECOND30;
            this.StartPoint = Core.TimeUtils.DateTimeUtcNow.AddDays(-10);
        }

        protected override void OnRun()
        {
            this.totalNetPl = 0D;

            // Restore symbol object from active connection
            if (this.CurrentSymbol != null && this.CurrentSymbol.State == BusinessObjectState.Fake)
                this.CurrentSymbol = Core.Instance.GetSymbol(this.CurrentSymbol.CreateInfo());

            if (this.CurrentSymbol == null)
            {
                this.Log("Incorrect input parameters... Symbol have not specified.", StrategyLoggingLevel.Error);
                return;
            }

            // Restore account object from active connection
            if (this.CurrentAccount != null && this.CurrentAccount.State == BusinessObjectState.Fake)
                this.CurrentAccount = Core.Instance.GetAccount(this.CurrentAccount.CreateInfo());

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

            this.orderTypeId = Core.OrderTypes.FirstOrDefault(x => x.ConnectionId == this.CurrentSymbol.ConnectionId && x.Behavior == OrderTypeBehavior.Market).Id;

            if (string.IsNullOrEmpty(this.orderTypeId))
            {
                this.Log("Connection of selected symbol has not support market orders", StrategyLoggingLevel.Error);
                return;
            }

            //this.indicatorFastMA = Core.Instance.Indicators.BuiltIn.SMA(this.FastMA, PriceType.Close);
            //this.indicatorSlowMA = Core.Instance.Indicators.BuiltIn.SMA(this.SlowMA, PriceType.Close);

            this.hdm = this.CurrentSymbol.GetHistory(this.Period, this.CurrentSymbol.HistoryType, this.StartPoint);

            Core.PositionAdded += this.Core_PositionAdded;
            Core.PositionRemoved += this.Core_PositionRemoved;

            Core.OrdersHistoryAdded += this.Core_OrdersHistoryAdded;

            Core.TradeAdded += this.Core_TradeAdded;

            this.hdm.HistoryItemUpdated += this.Hdm_HistoryItemUpdated;
            this.hdm.NewHistoryItem += this.Hdm_OnNewHistoryItem;

            //this.hdm.AddIndicator(this.indicatorFastMA);
            //this.hdm.AddIndicator(this.indicatorSlowMA);
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
        }

        private void Core_PositionAdded(Position obj)
        {
            var positions = Core.Instance.Positions.Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount).ToArray();
            this.longPositionsCount = positions.Count(x => x.Side == Side.Buy);
            this.shortPositionsCount = positions.Count(x => x.Side == Side.Sell);

            double currentPositionsQty = positions.Sum(x => x.Side == Side.Buy ? x.Quantity : -x.Quantity);

            if (Math.Abs(currentPositionsQty) == this.Quantity)
                this.waitOpenPosition = false;
        }

        private void Core_PositionRemoved(Position obj)
        {
            var positions = Core.Instance.Positions.Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount).ToArray();
            this.longPositionsCount = positions.Count(x => x.Side == Side.Buy);
            this.shortPositionsCount = positions.Count(x => x.Side == Side.Sell);

            if (!positions.Any())
                this.waitClosePositions = false;
            this.inPosition = false;
        }

        private void Core_OrdersHistoryAdded(OrderHistory obj)
        {
            if (obj.Symbol == this.CurrentSymbol)
                return;

            if (obj.Account == this.CurrentAccount)
                return;

            if (obj.Status == OrderStatus.Refused)
                this.ProcessTradingRefuse();
        }

        private void Core_TradeAdded(Trade obj)
        {
            if (obj.NetPnl != null)
                this.totalNetPl += obj.NetPnl.Value;

            if (obj.GrossPnl != null)
                this.totalGrossPl += obj.GrossPnl.Value;

            if (obj.Fee != null)
                this.totalFee += obj.Fee.Value;
        }

        private void Hdm_HistoryItemUpdated(object sender, HistoryEventArgs e) => this.OnUpdate();

        private void Hdm_OnNewHistoryItem(object sender, HistoryEventArgs args)
        {
            this.newBar = true;
        }
        private void OnUpdate()
        {
            /// Previous Open and Close potential code
            //lookback= 1
            //historicaldata.GetPrice(PriceType.Close, lookback)
            //double lastLow = historicaldata.GetPrice(PriceType.Low, 1);
            //double lastHigh = historicaldata.GetPrice(PriceType.High, 1);
            /// Previous Open and Close potential code

            /////////////// SMA Spread Variables \\\\\\\\\\\\\\\

            //double sma_10 = this.indicatorFastMA.GetValue(0);
            //double sma_20 = this.indicatorSlowMA.GetValue(0);
            //double diff_0 = Math.Abs(sma_10 - sma_20);

            //double sma_10_3 = this.indicatorFastMA.GetValue(3);
            //double sma_20_3 = this.indicatorSlowMA.GetValue(3);
            //double sma_10_4 = this.indicatorFastMA.GetValue(4);
            //double sma_20_4 = this.indicatorSlowMA.GetValue(4);
            //double sma_10_5 = this.indicatorFastMA.GetValue(5);
            //double sma_20_5 = this.indicatorSlowMA.GetValue(5);
            //double sma_10_6 = this.indicatorFastMA.GetValue(6);
            //double sma_20_6 = this.indicatorSlowMA.GetValue(6);
            //double sma_10_7 = this.indicatorFastMA.GetValue(7);
            //double sma_20_7 = this.indicatorSlowMA.GetValue(7);

            //double diff_3 = Math.Abs(sma_10_3 - sma_20_3);
            //double diff_4 = Math.Abs(sma_10_4 - sma_20_4);
            //double diff_5 = Math.Abs(sma_10_5 - sma_20_5);
            //double diff_6 = Math.Abs(sma_10_6 - sma_20_6);
            //double diff_7 = Math.Abs(sma_10_7 - sma_20_7);

            //double diff_sum = diff_3 + diff_4 + diff_5 + diff_6 + diff_7;
            //double diff_avg = diff_sum / 5;

            //this.Log($"{sma_10_3}");
            //this.Log($"{this.indicatorFastMA}");
            //this.Log($"{prevSide}");

            //this.Log($"Previous Side: {prevSide}");
            //this.Log($"In Position: {inPosition}");

            /////////////// SMA Spread Variables \\\\\\\\\\\\\\\

            //double price = HistoricalDataExtensions.Close(this.hdm, 0);
            ////double currentPrice = Core.Instance.Positions.Any(currentPrice);
            //double lastLow = HistoricalDataExtensions.Low(this.hdm, 1);
            //double lastHigh = HistoricalDataExtensions.High(this.hdm, 1);

            var positions = Core.Instance.Positions.Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount).ToArray();
            ////double pnlTicks = positions.Sum(x => x.GrossPnLTicks);

            //double sma_10_x = this.indicatorFastMA.GetValue(0);
            //double sma_20_x = this.indicatorSlowMA.GetValue(0);
            //double diff_0_x = Math.Abs(sma_10_x - sma_20_x);





            /// Consider getting the weighted value
            /// (Weighted (High+Low+Close+Close)/4 price)
            /// of every bar and comparing to the weighted value
            /// of the current bar for entry

            /////////////// Price Surge Variables \\\\\\\\\\\\\\\
            double open_0 = HistoricalDataExtensions.Open(this.hdm, 0);
            double close_0 = HistoricalDataExtensions.Close(this.hdm, 0);
            double bar_0 = Math.Abs(open_0 - close_0);

            double open_1 = HistoricalDataExtensions.Open(this.hdm, 1);
            double open_2 = HistoricalDataExtensions.Open(this.hdm, 2);
            double open_3 = HistoricalDataExtensions.Open(this.hdm, 3);
            double open_4 = HistoricalDataExtensions.Open(this.hdm, 4);
            double open_5 = HistoricalDataExtensions.Open(this.hdm, 5);
            double open_6 = HistoricalDataExtensions.Open(this.hdm, 6);
            double open_7 = HistoricalDataExtensions.Open(this.hdm, 7);
            double open_8 = HistoricalDataExtensions.Open(this.hdm, 8);
            double open_9 = HistoricalDataExtensions.Open(this.hdm, 9);
            double open_10 = HistoricalDataExtensions.Open(this.hdm, 10);

            double close_1 = HistoricalDataExtensions.Close(this.hdm, 1);
            double close_2 = HistoricalDataExtensions.Close(this.hdm, 2);
            double close_3 = HistoricalDataExtensions.Close(this.hdm, 3);
            double close_4 = HistoricalDataExtensions.Close(this.hdm, 4);
            double close_5 = HistoricalDataExtensions.Close(this.hdm, 5);
            double close_6 = HistoricalDataExtensions.Close(this.hdm, 6);
            double close_7 = HistoricalDataExtensions.Close(this.hdm, 7);
            double close_8 = HistoricalDataExtensions.Close(this.hdm, 8);
            double close_9 = HistoricalDataExtensions.Close(this.hdm, 9);
            double close_10 = HistoricalDataExtensions.Close(this.hdm, 10);


            double bar_1 = Math.Abs(open_1 - close_1);
            double bar_2 = Math.Abs(open_2 - close_2);
            double bar_3 = Math.Abs(open_3 - close_3);
            double bar_4 = Math.Abs(open_4 - close_4);
            double bar_5 = Math.Abs(open_5 - close_5);
            double bar_6 = Math.Abs(open_6 - close_6);
            double bar_7 = Math.Abs(open_7 - close_7);
            double bar_8 = Math.Abs(open_8 - close_8);
            double bar_9 = Math.Abs(open_9 - close_9);
            double bar_10 = Math.Abs(open_10 - close_10);

            double lookbackSum_10 = bar_1 + bar_2 + bar_3 + bar_4 + bar_5 + bar_6 + bar_7 + bar_8 + bar_9 + bar_10;
            double lookbackAvg_10 = lookbackSum_10 / 10;

            double lookbackSum_5 = bar_1 + bar_2 + bar_3 + bar_4 + bar_5;
            double lookbackAvg_5 = lookbackSum_5 / 5;



            //if (this.waitOpenPosition)
            //{
            //    return;
            //}

            //if (this.waitClosePositions)
            //{
            //    return;
            //}

            //if (this.prevSide == "buy")
            //{
            //    if (sma_10_x <= sma_20_x)
            //    {
            //        this.prevSide = "none";
            //    }
            //}
            //else if (this.prevSide == "sell")
            //{
            //    if (sma_10_x >= sma_20_x)
            //    {
            //        this.prevSide = "none";
            //    }
            //}

            //this.Log($"New Bar: {this.newBar}");
            //this.Log($"bar_0: {bar_0}");

            if (positions.Length != 0)
            {
                return;
                ////this.Log("Open Positions");
                ////return;
                ////var pnl = currentPosition.GrossPnLTicks;
                ////// Closing Positions
                //////if (this.indicatorFastMA.GetValue(1) < this.indicatorSlowMA.GetValue(1) || this.indicatorFastMA.GetValue(1) > this.indicatorSlowMA.GetValue(1)) 
                //double pnlTicks = positions.Sum(x => x.GrossPnLTicks);
                ////if (pnlTicks > 29 || pnlTicks < -9)
                ////{
                ////    this.waitClosePositions = true;
                ////    this.Log($"Start close positions ({positions.Length})");

                ////    foreach (var item in positions)
                ////    {
                ////        var result = item.Close();

                ////        if (result.Status == TradingOperationResultStatus.Failure)
                ////        {
                ////            this.Log($"Close positions refuse: {(string.IsNullOrEmpty(result.Message) ? result.Status : result.Message)}", StrategyLoggingLevel.Trading);
                ////            this.ProcessTradingRefuse();
                ////        }
                ////        else
                ////            this.Log($"Position was close: {result.Status}", StrategyLoggingLevel.Trading);
                ////    }
                ////}

                //double price_x = HistoricalDataExtensions.Close(this.hdm, 0);
                //double lastLow_x = HistoricalDataExtensions.Low(this.hdm, 1);
                //double lastHigh_x = HistoricalDataExtensions.High(this.hdm, 1);


                //// if (pnlTicks >= profitThreshold && tsInit == false)
                //// {
                ////   Cancel any previous stoploss orders
                ////   Create new trailing stop with trailingStop
                ////   Perhaps also add bool to indicate trailing stop has been initiated [tsInit]
                //// }


                //if (sma_10_x > sma_20_x)
                //{
                //    if (price_x < lastLow_x || pnlTicks > 150)
                //    {
                //        this.waitClosePositions = true;
                //        this.Log($"Start close positions ({positions.Length})");

                //        foreach (var item in positions)
                //        {
                //            //item.StopLoss.Cancel(); // Doesn't work. Actually breaks the position close. Gotta find another way to do this here.
                //            var result = item.Close();

                //            if (result.Status == TradingOperationResultStatus.Failure)
                //            {
                //                this.Log($"Close positions refuse: {(string.IsNullOrEmpty(result.Message) ? result.Status : result.Message)}", StrategyLoggingLevel.Trading);
                //                this.ProcessTradingRefuse();
                //            }
                //            else
                //            {
                //                this.Log($"Position was close: {result.Status}", StrategyLoggingLevel.Trading);
                //                this.inPosition = false;
                //            }
                //        }
                //    }
                //}
                //else if (sma_10_x < sma_20_x)
                //{
                //    if (price_x > lastHigh_x || pnlTicks > 150)
                //    {
                //        this.waitClosePositions = true;
                //        this.Log($"Start close positions ({positions.Length})");

                //        foreach (var item in positions)
                //        {
                //            //item.StopLoss.Cancel(); // Doesn't work. Actually breaks the position close. Gotta find another way to do this here.
                //            var result = item.Close();


                //            if (result.Status == TradingOperationResultStatus.Failure)
                //            {
                //                this.Log($"Close positions refuse: {(string.IsNullOrEmpty(result.Message) ? result.Status : result.Message)}", StrategyLoggingLevel.Trading);
                //                this.ProcessTradingRefuse();
                //            }
                //            else
                //            {
                //                this.Log($"Position was close: {result.Status}", StrategyLoggingLevel.Trading);
                //                this.inPosition = false;
                //            }
                //        }
                //    }
                //}
            }
            else // Opening New Positions
            {
                if (bar_0 > lookbackAvg_10 * this.multiplicative && this.inPosition == false && this.newBar == true)
                {
                    //this.Log("Arrow Signal");
                    //if (this.indicatorFastMA.GetValue(2) < this.indicatorSlowMA.GetValue(2) && this.indicatorFastMA.GetValue(1) > this.indicatorSlowMA.GetValue(1))
                    if (close_0 > open_0) // Green Bar
                    {
                        this.waitOpenPosition = true;
                        this.Log("Start open buy position");
                        var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters()
                        {
                            Account = this.CurrentAccount,
                            Symbol = this.CurrentSymbol,
                            TakeProfit = SlTpHolder.CreateTP(40, PriceMeasurement.Offset), // Added
                            StopLoss = SlTpHolder.CreateSL(20, PriceMeasurement.Offset), // Added
                            //StopLoss = SlTpHolder.CreateSL(this.trailingStop, PriceMeasurement.Offset, true),
                            OrderTypeId = this.orderTypeId,
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
                            this.inPosition = true;
                            this.newBar = false;
                            //this.prevSide = "buy";
                            //this.stopOrderId = result.OrderId; // Added from code to try and get stop order id for canceling stop order
                        }
                    }
                    //else if (this.indicatorFastMA.GetValue(2) > this.indicatorSlowMA.GetValue(2) && this.indicatorFastMA.GetValue(1) < this.indicatorSlowMA.GetValue(1))
                    else if (close_0 < open_0) // Red Bar
                    {
                        this.waitOpenPosition = true;
                        this.Log("Start open sell position");
                        var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters()
                        {
                            Account = this.CurrentAccount,
                            Symbol = this.CurrentSymbol,
                            TakeProfit = SlTpHolder.CreateTP(40, PriceMeasurement.Offset), // Added
                            StopLoss = SlTpHolder.CreateSL(20, PriceMeasurement.Offset), // Added
                            //StopLoss = SlTpHolder.CreateSL(this.trailingStop, PriceMeasurement.Offset, true),
                            OrderTypeId = this.orderTypeId,
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
                            this.inPosition = true;
                            this.newBar = false;
                            //this.prevSide = "sell";
                            //this.stopOrderId = result.OrderId; // Added from code to try and get stop order id for canceling stop order
                        }
                    }
                }
            }
        }

        private void ProcessTradingRefuse()
        {
            this.Log("Strategy have received refuse for trading action. It should be stopped", StrategyLoggingLevel.Error);
            this.Stop();
        }
    }
}