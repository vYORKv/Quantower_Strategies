using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using TradingPlatform.BusinessLayer;

namespace SimpleMACross
{
    public sealed class SimpleMACross : Strategy, ICurrentAccount, ICurrentSymbol
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
        [InputParameter("Fast MA", 2, minimum: 1, maximum: 100, increment: 1, decimalPlaces: 0)]
        public int FastMA { get; set; }

        /// <summary>
        /// Period for Slow MA indicator
        /// </summary>
        [InputParameter("Slow MA", 3, minimum: 1, maximum: 100, increment: 1, decimalPlaces: 0)]
        public int SlowMA { get; set; }

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

        public override string[] MonitoringConnectionsIds => new string[] { this.CurrentSymbol?.ConnectionId, this.CurrentAccount?.ConnectionId };

        private Indicator indicatorFastMA;
        private Indicator indicatorSlowMA;
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

        private double totalNetPl;
        private double totalGrossPl;
        private double totalFee;

        public SimpleMACross()
            : base()
        {
            this.Name = "SMA Cross Strategy";
            this.Description = "Raw strategy without any additional function";

            this.FastMA = 10;
            this.SlowMA = 20;
            this.Period = Period.MIN5;
            this.StartPoint = Core.TimeUtils.DateTimeUtcNow.AddDays(-100);
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

            this.indicatorFastMA = Core.Instance.Indicators.BuiltIn.SMA(this.FastMA, PriceType.Close);
            this.indicatorSlowMA = Core.Instance.Indicators.BuiltIn.SMA(this.SlowMA, PriceType.Close);

            this.hdm = this.CurrentSymbol.GetHistory(this.Period, this.CurrentSymbol.HistoryType, this.StartPoint);

            Core.PositionAdded += this.Core_PositionAdded;
            Core.PositionRemoved += this.Core_PositionRemoved;

            Core.OrdersHistoryAdded += this.Core_OrdersHistoryAdded;

            Core.TradeAdded += this.Core_TradeAdded;

            this.hdm.HistoryItemUpdated += this.Hdm_HistoryItemUpdated;

            this.hdm.AddIndicator(this.indicatorFastMA);
            this.hdm.AddIndicator(this.indicatorSlowMA);
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

            /////////////// SMA Spread Variables \\\\\\\\\\\\\\\

            //double price = HistoricalDataExtensions.Close(this.hdm, 0);
            ////double currentPrice = Core.Instance.Positions.Any(currentPrice);
            //double lastLow = HistoricalDataExtensions.Low(this.hdm, 1);
            //double lastHigh = HistoricalDataExtensions.High(this.hdm, 1);

            var positions = Core.Instance.Positions.Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount).ToArray();
            //double pnlTicks = positions.Sum(x => x.GrossPnLTicks);

            double sma_10_x = this.indicatorFastMA.GetValue(0);
            double sma_20_x = this.indicatorSlowMA.GetValue(0);
            double diff_0_x = Math.Abs(sma_10_x - sma_20_x);


            if (this.waitOpenPosition)
            {
                return;
            }

            if (this.waitClosePositions)
            {
                return;
            }

            if (this.prevSide == "buy")
            {
                if (sma_10_x <= sma_20_x)
                {
                    this.prevSide = "none";
                }
            }
            else if (this.prevSide == "sell")
            {
                if (sma_10_x >= sma_20_x)
                {
                    this.prevSide = "none";
                }
            }


            if (positions.Any())
            {
                //this.Log("Open Positions");
                //return;
                //var pnl = currentPosition.GrossPnLTicks;
                //// Closing Positions
                ////if (this.indicatorFastMA.GetValue(1) < this.indicatorSlowMA.GetValue(1) || this.indicatorFastMA.GetValue(1) > this.indicatorSlowMA.GetValue(1)) 
                double pnlTicks = positions.Sum(x => x.GrossPnLTicks);
                //if (pnlTicks > 29 || pnlTicks < -9)
                //{
                //    this.waitClosePositions = true;
                //    this.Log($"Start close positions ({positions.Length})");

                //    foreach (var item in positions)
                //    {
                //        var result = item.Close();

                //        if (result.Status == TradingOperationResultStatus.Failure)
                //        {
                //            this.Log($"Close positions refuse: {(string.IsNullOrEmpty(result.Message) ? result.Status : result.Message)}", StrategyLoggingLevel.Trading);
                //            this.ProcessTradingRefuse();
                //        }
                //        else
                //            this.Log($"Position was close: {result.Status}", StrategyLoggingLevel.Trading);
                //    }
                //}

                double price_x = HistoricalDataExtensions.Close(this.hdm, 0);
                double lastLow_x = HistoricalDataExtensions.Low(this.hdm, 1);
                double lastHigh_x = HistoricalDataExtensions.High(this.hdm, 1);

                if (sma_10_x > sma_20_x)
                {
                    if (price_x < lastLow_x || pnlTicks > 150)
                    {
                        this.waitClosePositions = true;
                        this.Log($"Start close positions ({positions.Length})");

                        foreach (var item in positions)
                        {
                            var result = item.Close();

                            if (result.Status == TradingOperationResultStatus.Failure)
                            {
                                this.Log($"Close positions refuse: {(string.IsNullOrEmpty(result.Message) ? result.Status : result.Message)}", StrategyLoggingLevel.Trading);
                                this.ProcessTradingRefuse();
                            }
                            else
                            {
                                this.Log($"Position was close: {result.Status}", StrategyLoggingLevel.Trading);
                                this.inPosition = false;
                            }   
                        }
                    }
                }
                else if (sma_10_x < sma_20_x)
                {
                    if (price_x > lastHigh_x || pnlTicks > 150)
                    {
                        this.waitClosePositions = true;
                        this.Log($"Start close positions ({positions.Length})");

                        foreach (var item in positions)
                        {
                            var result = item.Close();

                            if (result.Status == TradingOperationResultStatus.Failure)
                            {
                                this.Log($"Close positions refuse: {(string.IsNullOrEmpty(result.Message) ? result.Status : result.Message)}", StrategyLoggingLevel.Trading);
                                this.ProcessTradingRefuse();
                            }
                            else
                            {
                                this.Log($"Position was close: {result.Status}", StrategyLoggingLevel.Trading);
                                this.inPosition = false;
                            }  
                        }
                    }
                }
            }
            else // Opening New Positions
            {
                double testSMA = this.indicatorFastMA.GetValue(2);

                double sma_10 = this.indicatorFastMA.GetValue(0);
                double sma_20 = this.indicatorSlowMA.GetValue(0);
                double diff_0 = Math.Abs(sma_10 - sma_20);

                double sma_10_3 = this.indicatorFastMA.GetValue(3);
                double sma_20_3 = this.indicatorSlowMA.GetValue(3);
                double sma_10_4 = this.indicatorFastMA.GetValue(4);
                double sma_20_4 = this.indicatorSlowMA.GetValue(4);
                double sma_10_5 = this.indicatorFastMA.GetValue(5);
                double sma_20_5 = this.indicatorSlowMA.GetValue(5);
                double sma_10_6 = this.indicatorFastMA.GetValue(6);
                double sma_20_6 = this.indicatorSlowMA.GetValue(6);
                double sma_10_7 = this.indicatorFastMA.GetValue(7);
                double sma_20_7 = this.indicatorSlowMA.GetValue(7);

                double diff_3 = Math.Abs(sma_10_3 - sma_20_3);
                double diff_4 = Math.Abs(sma_10_4 - sma_20_4);
                double diff_5 = Math.Abs(sma_10_5 - sma_20_5);
                double diff_6 = Math.Abs(sma_10_6 - sma_20_6);
                double diff_7 = Math.Abs(sma_10_7 - sma_20_7);

                double diff_sum = diff_3 + diff_4 + diff_5 + diff_6 + diff_7;
                double diff_avg = diff_sum / 5;

                //this.Log($"Test SMA : {testSMA}");

                this.Log($"diff_0: {diff_0}");
                this.Log($"diff_avg: {diff_avg}");
                //this.Log($"sma_10: {sma_10}");
                //this.Log($"prevSide: {this.prevSide}");
                //this.Log($"inPosition: {this.inPosition}");

                if (diff_0 > diff_avg * 2.0 && this.inPosition == false && prevSide == "none")
                {
                    this.Log("Arrow Signal");
                    //if (this.indicatorFastMA.GetValue(2) < this.indicatorSlowMA.GetValue(2) && this.indicatorFastMA.GetValue(1) > this.indicatorSlowMA.GetValue(1))
                    if (sma_10 > sma_20)
                    {
                        this.waitOpenPosition = true;
                        this.Log("Start open buy position");
                        var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters()
                        {
                            Account = this.CurrentAccount,
                            Symbol = this.CurrentSymbol,
                            //TakeProfit = SlTpHolder.CreateTP(30, PriceMeasurement.Offset), // Added
                            //StopLoss = SlTpHolder.CreateSL(10, PriceMeasurement.Offset), // Added
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
                            this.prevSide = "buy";
                        }
                    }
                    //else if (this.indicatorFastMA.GetValue(2) > this.indicatorSlowMA.GetValue(2) && this.indicatorFastMA.GetValue(1) < this.indicatorSlowMA.GetValue(1))
                    else if (sma_10 < sma_20)
                    {
                        this.waitOpenPosition = true;
                        this.Log("Start open sell position");
                        var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters()
                        {
                            Account = this.CurrentAccount,
                            Symbol = this.CurrentSymbol,
                            //TakeProfit = SlTpHolder.CreateTP(30, PriceMeasurement.Offset), // Added
                            //StopLoss = SlTpHolder.CreateSL(10, PriceMeasurement.Offset), // Added
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
                            this.prevSide = "sell";
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