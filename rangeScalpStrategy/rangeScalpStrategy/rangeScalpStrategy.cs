using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using TradingPlatform.BusinessLayer;

namespace RangeScalp
{
    public sealed class RangeScalp : Strategy, ICurrentAccount, ICurrentSymbol
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

        [InputParameter("Take Profit")]
        public int takeProfit = 6;

        [InputParameter("Stop Loss")]
        public int stopLoss = 40;

        [InputParameter("Max Profit")]
        public double maxProfit = 200.0;

        [InputParameter("Max Loss")]
        public double maxLoss = -200.0;


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
        private int tradeCount = 0;
        private double rangeHigh = 0.0;
        private double rangeLow = 999999.0;
        private bool buyPermitted = false;
        private bool sellPermitted = false;
        private bool openBuy = false;
        private bool openSell = false;

        private double totalNetPl;
        private double totalGrossPl;
        private double totalFee;

        private string stopOrderId = default; // Added from code to cancel order and get id

        public RangeScalp()
            : base()
        {
            this.Name = "Quantavius_RS";
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

            if (positions.Length == 0)
                this.waitClosePositions = false;
            this.inPosition = false;
            this.openBuy = false;
            this.openSell = false;
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
            double close_1 = HistoricalDataExtensions.Close(this.hdm, 1);
            double open_1 = HistoricalDataExtensions.Open(this.hdm, 1);
            if (close_1 > open_1) // Green bar
            {
                this.sellPermitted = true;
            }
            else if (close_1 < open_1) // Red bar
            {
                this.buyPermitted = true;
            }
            this.newBar = true;
        }
        private void OnUpdate()
        {
            double price = HistoricalDataExtensions.Close(this.hdm, 0);

            if (price >= this.rangeHigh)
            {
                this.rangeHigh = price;
            }
            if (price <= this.rangeLow)
            {
                this.rangeLow = price;
            }

            this.Log($"Buy Permitted: {buyPermitted}");
            this.Log($"Sell Permitted: {sellPermitted}");
            //this.Log($"Range High: {this.rangeHigh}");
            //this.Log($"Range Low: {this.rangeLow}");
            //this.Log($"Price: {price}");
            //if (totalGrossPl >= 400 || totalGrossPl <= -200)
            //if (totalGrossPl >= 1000)
            //if (this.tradeCount >= 100)
            if (totalGrossPl >= this.maxProfit || this.totalGrossPl <= this.maxLoss)
            {
                return;
            }
            else
            {
                var positions = Core.Instance.Positions.Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount).ToArray();
                double pnlTicks = positions.Sum(x => x.GrossPnLTicks);

                if (positions.Length != 0) //&& (pnlTicks / this.Quantity >= 6 || pnlTicks / this.Quantity <= 5))
                {
                    return;
                    //this.waitClosePositions = true;
                    //this.Log($"Start close positions ({positions.Length})");

                    //foreach (var item in positions)
                    //{
                    //    //item.StopLoss.Cancel(); // Doesn't work. Actually breaks the position close. Gotta find another way to do this here.
                    //    var result = item.Close();

                    //    if (result.Status == TradingOperationResultStatus.Failure)
                    //    {
                    //        this.Log($"Close positions refuse: {(string.IsNullOrEmpty(result.Message) ? result.Status : result.Message)}", StrategyLoggingLevel.Trading);
                    //        this.ProcessTradingRefuse();
                    //    }
                    //    else
                    //    {
                    //        this.Log($"Position was close: {result.Status}", StrategyLoggingLevel.Trading);
                    //        this.inPosition = false;
                    //    }
                    //}
                }
                else // Opening New Positions
                {
                    //if (this.inPosition == false) //&& this.newBar == true
                    if (true)
                    {
                        if (this.buyPermitted == true) // might need price >= rangeHigh //  && price >= rangeHigh
                        {
                            this.waitOpenPosition = true;
                            this.Log("Start open buy position");
                            var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters()
                            {
                                Account = this.CurrentAccount,
                                Symbol = this.CurrentSymbol,
                                TakeProfit = SlTpHolder.CreateTP(takeProfit, PriceMeasurement.Offset),
                                StopLoss = SlTpHolder.CreateSL(stopLoss, PriceMeasurement.Offset),
                                TriggerPrice = this.rangeHigh + .50,
                                Quantity = this.Quantity,
                                Side = Side.Buy,
                                OrderTypeId = OrderType.Stop
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
                                this.buyPermitted = false;
                                this.tradeCount += 1;
                                this.openBuy = true;
                                //this.prevSide = "buy";
                                //this.stopOrderId = result.OrderId; // Added from code to try and get stop order id for canceling stop order
                            }
                        }
                        if (this.sellPermitted == true) // might need price <= rangeLow // && price <= rangeLow
                        {
                            this.waitOpenPosition = true;
                            this.Log("Start open sell position");
                            var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters()
                            {
                                Account = this.CurrentAccount,
                                Symbol = this.CurrentSymbol,
                                TakeProfit = SlTpHolder.CreateTP(takeProfit, PriceMeasurement.Offset),
                                StopLoss = SlTpHolder.CreateSL(stopLoss, PriceMeasurement.Offset),
                                TriggerPrice = this.rangeLow - .50,
                                Quantity = this.Quantity,
                                Side = Side.Sell,
                                OrderTypeId = OrderType.Stop
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
                                this.sellPermitted = false;
                                this.tradeCount += 1;
                                this.openSell = true;
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