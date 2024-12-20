﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace boxRangeStrategy
{
    public sealed class boxRangeStrategy : Strategy, ICurrentAccount, ICurrentSymbol
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

        //[InputParameter("Stop Loss")]
        //public int stopLoss = 10;

        //[InputParameter("Take Profit")]
        //public int takeProfit = 5;

        [InputParameter("Max Trades")]
        public int maxTrades = 20;

        [InputParameter("Max Profit")]
        public int maxProfit = 1000;

        [InputParameter("Max Loss")]
        public int maxLoss = 500;

        [InputParameter("Range Offset (in ticks)")]
        public int rangeOffsetTicks = 0;

        [InputParameter("Half Range")]
        public bool halfRange = false;

        [InputParameter("Updates Before Initialization")]
        public int updateCounter = 1000;

        [InputParameter("Stop Orders")]
        public bool stopOrders = false;

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
        private bool halfBuyPlaced = false;
        private bool halfSellPlaced = false;
        private double price = 0.0;
        private double rangeHigh = 0.0;
        private double rangeLow = 999999.0;
        private double halfRangeHigh = 0.0;
        private double halfRangeLow = 999999.0;
        private int initCounter = 0;
        private bool insideRange = false;
        private bool insideHalfRange  = false;
        private string firstPosition = "none";

        public boxRangeStrategy()
            : base()
        {
            this.Name = "Quantavius_BR";
            this.Description = "Box Range Algo";

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
                //this.halfSellPlaced = false;
                //this.halfBuyPlaced = false;
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
            //double close_1 = HistoricalDataExtensions.Close(this.hdm, 1);
            //double open_1 = HistoricalDataExtensions.Open(this.hdm, 1);
            if (this.sellPlaced == true || this.buyPlaced == true)
            {
                return;
            }
            if (this.halfRange == false)
            {
                double rangeTotal = this.rangeHigh - this.rangeLow;
                double midpoint = rangeTotal / 2;
                double midpointRounded = Math.Round(midpoint * 4, MidpointRounding.ToEven) / 4;
                double bracketInTicks = midpointRounded / .25;
                this.Log($"Range Total: {rangeTotal}");
                this.Log($"Midpoint: {midpoint}");
                this.Log($"Midpoint Rounded: {midpointRounded}");
                this.Log($"Bracket in Ticks: {bracketInTicks}");
                //if (close_1 > open_1 && this.sellPlaced == false) // Green bar
                if (this.insideRange == true)
                {
                    if (this.stopOrders == false) // && this.firstPosition != "buy"
                    {
                        this.waitOpenPosition = true;
                        this.Log("Start open sell position");
                        var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters()
                        {
                            Account = this.CurrentAccount,
                            Symbol = this.CurrentSymbol,
                            TakeProfit = SlTpHolder.CreateTP(bracketInTicks + (this.rangeOffsetTicks * .25), PriceMeasurement.Offset),
                            StopLoss = SlTpHolder.CreateSL(bracketInTicks - (this.rangeOffsetTicks * .25), PriceMeasurement.Offset),
                            Price = this.rangeHigh - (this.rangeOffsetTicks * .25),
                            OrderTypeId = OrderType.Limit,
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
                    else if (this.stopOrders == true) // && this.firstPosition != "buy"
                    {
                        this.waitOpenPosition = true;
                        this.Log("Start open sell position");
                        var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters()
                        {
                            Account = this.CurrentAccount,
                            Symbol = this.CurrentSymbol,
                            TakeProfit = SlTpHolder.CreateTP(bracketInTicks + (this.rangeOffsetTicks * .25), PriceMeasurement.Offset),
                            StopLoss = SlTpHolder.CreateSL(bracketInTicks - (this.rangeOffsetTicks * .25), PriceMeasurement.Offset),
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
                }
                //else if (close_1 < open_1 && this.buyPlaced == false) // Red bar
                if (this.insideRange == true)
                {
                    if (this.stopOrders == false) // && this.firstPosition != "sell"
                    {
                        this.waitOpenPosition = true;
                        this.Log("Start open buy position");
                        var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters()
                        {
                            Account = this.CurrentAccount,
                            Symbol = this.CurrentSymbol,
                            TakeProfit = SlTpHolder.CreateTP(bracketInTicks - (this.rangeOffsetTicks * .25), PriceMeasurement.Offset),
                            StopLoss = SlTpHolder.CreateSL(bracketInTicks + (this.rangeOffsetTicks * .25), PriceMeasurement.Offset),
                            Price = this.rangeLow + (this.rangeOffsetTicks * .25),
                            OrderTypeId = OrderType.Limit,
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
                    else if (this.stopOrders == true) // && this.firstPosition != "sell"
                    {
                        this.waitOpenPosition = true;
                        this.Log("Start open buy position");
                        var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters()
                        {
                            Account = this.CurrentAccount,
                            Symbol = this.CurrentSymbol,
                            TakeProfit = SlTpHolder.CreateTP(bracketInTicks - (this.rangeOffsetTicks * .25), PriceMeasurement.Offset),
                            StopLoss = SlTpHolder.CreateSL(bracketInTicks + (this.rangeOffsetTicks * .25), PriceMeasurement.Offset),
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
                }
            }
            else if (this.halfRange == true)
            {
                double halfRangeTotal = this.halfRangeHigh - this.halfRangeLow;
                double halfMidpoint = halfRangeTotal / 2;
                double halfMidpointRounded = Math.Round(halfMidpoint * 4, MidpointRounding.ToEven) / 4;
                double halfBracketInTicks = halfMidpointRounded / .25;
                this.Log($"Range Total: {halfRangeTotal}");
                this.Log($"Midpoint: {halfMidpoint}");
                this.Log($"Midpoint Rounded: {halfMidpointRounded}");
                this.Log($"Bracket in Ticks: {halfBracketInTicks}");
                //if (close_1 > open_1 && this.halfSellPlaced == false && this.halfRangeLow != this.rangeLow) // Green bar
                if (this.insideHalfRange == true)
                {
                    if (this.stopOrders == false) // && this.firstPosition != "buy"
                    {
                        this.waitOpenPosition = true;
                        this.Log("Start open sell position");
                        var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters()
                        {
                            Account = this.CurrentAccount,
                            Symbol = this.CurrentSymbol,
                            TakeProfit = SlTpHolder.CreateTP(halfBracketInTicks + (this.rangeOffsetTicks * .25), PriceMeasurement.Offset),
                            StopLoss = SlTpHolder.CreateSL(halfBracketInTicks - (this.rangeOffsetTicks * .25), PriceMeasurement.Offset),
                            Price = this.halfRangeHigh - (this.rangeOffsetTicks * .25),
                            OrderTypeId = OrderType.Limit,
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
                    else if (this.stopOrders == true) // && this.firstPosition != "buy"
                    {
                        this.waitOpenPosition = true;
                        this.Log("Start open sell position");
                        var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters()
                        {
                            Account = this.CurrentAccount,
                            Symbol = this.CurrentSymbol,
                            TakeProfit = SlTpHolder.CreateTP(halfBracketInTicks + (this.rangeOffsetTicks * .25), PriceMeasurement.Offset),
                            StopLoss = SlTpHolder.CreateSL(halfBracketInTicks - (this.rangeOffsetTicks * .25), PriceMeasurement.Offset),
                            TriggerPrice = this.halfRangeLow - (this.rangeOffsetTicks * .25),
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
                            this.halfSellPlaced = true;
                        }
                    }
                }
                //else if (close_1 < open_1 && this.halfBuyPlaced == false && this.halfRangeHigh != this.rangeHigh) // Red bar
                if (this.insideHalfRange == true)
                {
                    if (this.stopOrders == false) // && this.firstPosition != "sell"
                    {
                        this.waitOpenPosition = true;
                        this.Log("Start open buy position");
                        var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters()
                        {
                            Account = this.CurrentAccount,
                            Symbol = this.CurrentSymbol,
                            TakeProfit = SlTpHolder.CreateTP(halfBracketInTicks - (this.rangeOffsetTicks * .25), PriceMeasurement.Offset),
                            StopLoss = SlTpHolder.CreateSL(halfBracketInTicks + (this.rangeOffsetTicks * .25), PriceMeasurement.Offset),
                            Price = this.halfRangeLow + (this.rangeOffsetTicks * .25),
                            OrderTypeId = OrderType.Limit,
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
                    else if (this.stopOrders == true) // && this.firstPosition != "sell"
                    {
                        this.waitOpenPosition = true;
                        this.Log("Start open buy position");
                        var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters()
                        {
                            Account = this.CurrentAccount,
                            Symbol = this.CurrentSymbol,
                            TakeProfit = SlTpHolder.CreateTP(halfBracketInTicks - (this.rangeOffsetTicks * .25), PriceMeasurement.Offset),
                            StopLoss = SlTpHolder.CreateSL(halfBracketInTicks + (this.rangeOffsetTicks * .25), PriceMeasurement.Offset),
                            TriggerPrice = this.halfRangeHigh + (this.rangeOffsetTicks * .25),
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
                            this.halfBuyPlaced = true;
                        }
                    }
                }
            }
        }

        private void Hdm_HistoryItemUpdated(object sender, HistoryEventArgs e) => this.OnUpdate();

        private void OnUpdate()
        {
            //if (this.firstPosition == "none")
            //{
            //    var positions = Core.Instance.Positions.Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount).ToArray();
            //    int totalBuys = positions.Count(x => x.Side == Side.Buy);
            //    int totalSells = positions.Count(x => x.Side == Side.Buy);
            //    if (totalBuys > 0)
            //    {
            //        this.firstPosition = "buy";
            //    }
            //    else if (totalSells > 0)
            //    {
            //        this.firstPosition = "sell";
            //    }
            //}

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

            double open_1 = HistoricalDataExtensions.Open(this.hdm, 1);
            double close_1 = HistoricalDataExtensions.Close(this.hdm, 1);
            double open_2 = HistoricalDataExtensions.Open(this.hdm, 2);
            double close_2 = HistoricalDataExtensions.Close(this.hdm, 2);

            double high_0 = HistoricalDataExtensions.High(this.hdm, 0);
            double low_0 = HistoricalDataExtensions.Low(this.hdm, 0);
            double high_1 = HistoricalDataExtensions.High(this.hdm, 1);
            double low_1 = HistoricalDataExtensions.Low(this.hdm, 1);
            double high_2 = HistoricalDataExtensions.High(this.hdm, 2);
            double low_2 = HistoricalDataExtensions.Low(this.hdm, 2);
            double high_3 = HistoricalDataExtensions.High(this.hdm, 3);
            double low_3 = HistoricalDataExtensions.Low(this.hdm, 3);
            double high_4 = HistoricalDataExtensions.High(this.hdm, 4);
            double low_4 = HistoricalDataExtensions.Low(this.hdm, 4);
            double high_5 = HistoricalDataExtensions.High(this.hdm, 5);
            double low_5 = HistoricalDataExtensions.Low(this.hdm, 5);
            double high_6 = HistoricalDataExtensions.High(this.hdm, 6);
            double low_6 = HistoricalDataExtensions.Low(this.hdm, 6);
            double high_7 = HistoricalDataExtensions.High(this.hdm, 7);
            double low_7 = HistoricalDataExtensions.Low(this.hdm, 7);
            double high_8 = HistoricalDataExtensions.High(this.hdm, 8);
            double low_8 = HistoricalDataExtensions.Low(this.hdm, 8);
            double high_9 = HistoricalDataExtensions.High(this.hdm, 9);
            double low_9 = HistoricalDataExtensions.Low(this.hdm, 9);
            double high_10 = HistoricalDataExtensions.High(this.hdm, 10);
            double low_10 = HistoricalDataExtensions.Low(this.hdm, 10);
            double high_11 = HistoricalDataExtensions.High(this.hdm, 11);
            double low_11 = HistoricalDataExtensions.Low(this.hdm, 11);

            //double[] fullRanges = [high_1, low_1, high_2, low_2, high_3, low_3, high_4, low_4, high_5, low_5, high_6, low_6, high_7, low_7, high_8, low_8, high_9, low_9, high_10, low_10];
            //double[] halfRanges = [high_1, low_1, high_2, low_2, high_3, low_3, high_4, low_4, high_5, low_5];

            double[] fullRanges = [high_2, low_2, high_3, low_3, high_4, low_4, high_5, low_5, high_6, low_6, high_7, low_7, high_8, low_8, high_9, low_9, high_10, low_10, high_11, low_11];
            double[] halfRanges = [high_2, low_2, high_3, low_3, high_4, low_4, high_5, low_5, high_6, low_6];

            if (this.halfRange == false)
            {
                for (int i = 0; i < fullRanges.Length; i++)
                {
                    if (fullRanges[i] >= this.rangeHigh)
                    {
                        this.rangeHigh = fullRanges[i];
                    }
                    if (fullRanges[i] <= this.rangeLow)
                    {
                        this.rangeLow = fullRanges[i];
                    }
                }
                //if (close_1 < this.rangeHigh && open_1 < this.rangeHigh && close_1 > this.rangeLow && open_1 > this.rangeLow)
                if (high_1 < this.rangeHigh && low_1 > this.rangeLow)
                //if (high_1 < this.rangeHigh && low_1 > this.rangeLow && open_2 < this.rangeHigh && close_2 < this.rangeHigh && open_2 > this.rangeLow && close_2 > this.rangeLow)
                {
                    if (price < this.rangeHigh && price > this.rangeLow) //  && high_0 < this.rangeHigh && low_0 > this.rangeLow
                    {
                        this.insideRange = true;
                    }
                    else
                    {
                        this.insideRange = false;
                    }
                }
                else
                {
                    this.insideRange = false;
                }
            }
            else if (this.halfRange == true)
            {
                for (int i = 0; i < halfRanges.Length; i++)
                {
                    if (halfRanges[i] >= this.halfRangeHigh)
                    {
                        this.halfRangeHigh = halfRanges[i];
                    }
                    if (halfRanges[i] <= this.halfRangeLow)
                    {
                        this.halfRangeLow = halfRanges[i];
                    }
                }
                //if (close_1 < this.halfRangeHigh && open_1 < this.halfRangeHigh && close_1 > this.halfRangeLow && open_1 > this.halfRangeLow)
                if (high_1 < this.halfRangeHigh && low_1 > this.halfRangeLow)
                //if (high_1 < this.halfRangeHigh && low_1 > this.halfRangeLow && open_2 < this.halfRangeHigh && close_2 < this.halfRangeHigh && open_2 > this.halfRangeLow && close_2 > this.halfRangeLow)
                {
                    if (price < this.halfRangeHigh && price > this.halfRangeLow) // && high_0 < this.halfRangeHigh && low_0 > this.halfRangeLow
                    {
                        this.insideHalfRange = true;
                    }
                    else
                    {
                        this.insideHalfRange = false;
                    }
                }
                else
                {
                    this.insideHalfRange = false;
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