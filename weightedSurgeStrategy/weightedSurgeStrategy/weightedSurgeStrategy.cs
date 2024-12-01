using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using TradingPlatform.BusinessLayer;

namespace WeightedSurge
{
    public sealed class WeightedSurge : Strategy, ICurrentAccount, ICurrentSymbol
    {
        [InputParameter("Symbol", 0)]
        public Symbol CurrentSymbol { get; set; }

        /// <summary>
        /// Account to place orders
        /// </summary>
        [InputParameter("Account", 1)]
        public Account CurrentAccount { get; set; }

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

        public WeightedSurge()
            : base()
        {
            this.Name = "Quantavius_WS";
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
            this.Log($"{totalGrossPl}");
            //if (totalGrossPl >= 400 || totalGrossPl <= -200)
            if(totalGrossPl >= 1000)
            {
                return;
            }
            else
            {
                var positions = Core.Instance.Positions.Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount).ToArray();
                ////double pnlTicks = positions.Sum(x => x.GrossPnLTicks);


                /////////////// Weighted Surge Variables \\\\\\\\\\\\\\\
                double close_0 = HistoricalDataExtensions.Close(this.hdm, 0);
                double open_0 = HistoricalDataExtensions.Open(this.hdm, 0);

                double weighted_0 = HistoricalDataExtensions.Weighted(this.hdm, 0);
                double weighted_1 = HistoricalDataExtensions.Weighted(this.hdm, 1);
                double weighted_2 = HistoricalDataExtensions.Weighted(this.hdm, 2);
                double weighted_3 = HistoricalDataExtensions.Weighted(this.hdm, 3);
                double weighted_4 = HistoricalDataExtensions.Weighted(this.hdm, 4);
                double weighted_5 = HistoricalDataExtensions.Weighted(this.hdm, 5);
                double weighted_6 = HistoricalDataExtensions.Weighted(this.hdm, 6);
                double weighted_7 = HistoricalDataExtensions.Weighted(this.hdm, 7);
                double weighted_8 = HistoricalDataExtensions.Weighted(this.hdm, 8);
                double weighted_9 = HistoricalDataExtensions.Weighted(this.hdm, 9);
                double weighted_10 = HistoricalDataExtensions.Weighted(this.hdm, 10);

                double lookbackSum_10 = weighted_1 + weighted_2 + weighted_3 + weighted_4 + weighted_5 + weighted_6 + weighted_7 + weighted_8 + weighted_9 + weighted_10;
                double lookbackAvg_10 = lookbackSum_10 / 10;

                //this.Log($"Weighted_0: {weighted_0}");
                //this.Log($"lookbackAvg_10: {lookbackAvg_10}");

                if (positions.Length != 0)
                {
                    return;
                }
                else // Opening New Positions
                {
                    if (weighted_0 > lookbackAvg_10 * this.multiplicative && this.inPosition == false && this.newBar == true)
                    {
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
        }

        private void ProcessTradingRefuse()
        {
            this.Log("Strategy have received refuse for trading action. It should be stopped", StrategyLoggingLevel.Error);
            this.Stop();
        }
    }
}