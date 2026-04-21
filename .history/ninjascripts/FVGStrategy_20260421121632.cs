#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// FVGStrategy — Full port of fvg_bot.py to NinjaScript.
//
// Detects Fair Value Gaps on a secondary hourly bar series and watches the
// primary (tick/minute) series for price to retest an FVG zone. On retest:
//   - Bullish FVG (gap UP, unfilled space below) → SHORT entry
//   - Bearish FVG (gap DOWN, unfilled space above) → LONG entry
//
// No CSV files. No Python. No polling.
// All data comes from NinjaTrader's native bar feeds.

namespace NinjaTrader.NinjaScript.Strategies
{
    public class FVGStrategy : Strategy
    {
        // ── FVG zone tracking ────────────────────────────────────────────────
        private sealed class FVGZone
        {
            public bool   IsBullish;       // true = gap up (SHORT opportunity)
            public double Top;
            public double Bottom;
            public double GapSize;
            public int    BarIndex;        // hourly bar index when zone was created
            public bool   Filled;
            public bool   TradeTaken;
            public int    TradeBarIndex;   // hourly bar index when last trade fired
            public bool   PriceWasOutside; // price must leave zone before re-entry triggers
        }

        private readonly List<FVGZone> _zones = new List<FVGZone>();

        // ── OCO order references ─────────────────────────────────────────────
        private Order _slOrder;
        private Order _tpOrder;

        // ── Secondary series index (hourly bars) ────────────────────────────
        private int _hourlyBarsIdx = 1;

        // ── Stats ────────────────────────────────────────────────────────────
        private int    _totalTrades;
        private int    _wins;
        private int    _losses;
        private double _totalPnl;

        // =====================================================================
        //  OnStateChange
        // =====================================================================
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description  = "FVG retest strategy — detects Fair Value Gaps on hourly bars "
                             + "and enters on price retests. No CSV files required.";
                Name         = "FVGStrategy";
                Calculate    = Calculate.OnEachTick;

                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds    = 30;
                EntriesPerDirection          = 1;
                EntryHandling                = EntryHandling.AllEntries;
                IsFillLimitOnTouch           = false;
                MaximumBarsLookBack          = MaximumBarsLookBack.Infinite;
                OrderFillResolution          = OrderFillResolution.Standard;
                Slippage                     = 0;
                StartBehavior                = StartBehavior.WaitUntilFlat;
                TimeInForce                  = TimeInForce.Gtc;
                RealtimeErrorHandling        = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling           = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade          = 5;
                IsInstantiatedOnEachOptimizationIteration = false;

                // FVG detection
                MinGapSizePoints = 5.0;
                MaxZoneAgeBars   = 1000;
                MaxZoneDistance  = 250.0;

                // Execution
                ContractQuantity   = 1;
                StopLossTicks      = 40;
                ProfitTargetTicks  = 40;
                BarsCooldown       = 1;

                // Display
                ShowStats = true;
            }
            else if (State == State.Configure)
            {
                // Add hourly data series for FVG detection.
                // The primary series (BarsInProgress == 0) is whatever the chart uses
                // for live price watching and order execution.
                AddDataSeries(BarsPeriodType.Minute, 60);
            }
            else if (State == State.DataLoaded)
            {
                _zones.Clear();
                _slOrder      = null;
                _tpOrder      = null;
                _totalTrades  = 0;
                _wins         = 0;
                _losses       = 0;
                _totalPnl     = 0;
            }
        }

        // =====================================================================
        //  OnBarUpdate
        // =====================================================================
        protected override void OnBarUpdate()
        {
            // ── Hourly series: FVG detection ──────────────────────────────────
            if (BarsInProgress == _hourlyBarsIdx)
            {
                if (CurrentBars[_hourlyBarsIdx] < 3)
                    return;

                ProcessHourlyBar();
                return;
            }

            // ── Primary series: live price watching ───────────────────────────
            if (BarsInProgress != 0)
                return;

            if (CurrentBar < BarsRequiredToTrade)
                return;

            double price = Close[0];

            // Check zone fills and retest signals on every tick
            CheckZoneFills(price);
            CheckRetestSignals(price);

            // On-chart stats
            if (ShowStats && IsFirstTickOfBar)
                DrawStats(price);
        }

        // =====================================================================
        //  Hourly bar processing — detect new FVGs and expire old zones
        // =====================================================================
        private void ProcessHourlyBar()
        {
            int bar = CurrentBars[_hourlyBarsIdx];

            // Re-enable cooldowns from previous bar
            foreach (var z in _zones)
            {
                if (!z.Filled && z.TradeTaken && z.TradeBarIndex >= 0
                    && bar > z.TradeBarIndex + BarsCooldown)
                {
                    z.TradeTaken      = false;
                    z.PriceWasOutside = true;
                }
            }

            // 3-bar FVG pattern on hourly series
            double high1 = Highs[_hourlyBarsIdx][2];
            double low1  = Lows[_hourlyBarsIdx][2];
            double low3  = Lows[_hourlyBarsIdx][0];
            double high3 = Highs[_hourlyBarsIdx][0];

            // Bullish FVG: candle3.Low > candle1.High → gap above candle1, below candle3
            if (low3 > high1)
            {
                double gapSize = low3 - high1;
                if (gapSize >= MinGapSizePoints)
                    TryAddZone(true, high1, low3, gapSize, bar);
            }
            // Bearish FVG: candle3.High < candle1.Low → gap below candle1, above candle3
            else if (high3 < low1)
            {
                double gapSize = low1 - high3;
                if (gapSize >= MinGapSizePoints)
                    TryAddZone(false, high3, low1, gapSize, bar);
            }

            // Mark zones filled by completed hourly bars
            foreach (var z in _zones)
            {
                if (z.Filled) continue;
                if (z.IsBullish && Lows[_hourlyBarsIdx][0] <= z.Bottom)
                    z.Filled = true;
                else if (!z.IsBullish && Highs[_hourlyBarsIdx][0] >= z.Top)
                    z.Filled = true;
            }

            // Remove filled and expired zones
            _zones.RemoveAll(z => z.Filled || (bar - z.BarIndex) > MaxZoneAgeBars);
        }

        // =====================================================================
        //  Add zone, suppressing duplicates (keep smaller overlapping zone)
        // =====================================================================
        private void TryAddZone(bool isBullish, double bottom, double top, double gapSize, int barIdx)
        {
            var toRemove = new List<FVGZone>();

            foreach (var z in _zones)
            {
                if (z.IsBullish != isBullish) continue;
                if (z.Filled)                 continue;

                // Check overlap
                if (z.Bottom >= top || bottom >= z.Top) continue;

                if (gapSize < z.GapSize)
                    toRemove.Add(z);   // new zone is smaller — replace existing
                else
                    return;            // existing zone is smaller — discard new one
            }

            foreach (var z in toRemove)
                _zones.Remove(z);

            _zones.Add(new FVGZone
            {
                IsBullish      = isBullish,
                Top            = top,
                Bottom         = bottom,
                GapSize        = gapSize,
                BarIndex       = barIdx,
                Filled         = false,
                TradeTaken     = false,
                TradeBarIndex  = -1,
                PriceWasOutside = true,
            });
        }

        // =====================================================================
        //  Live price zone fill check
        // =====================================================================
        private void CheckZoneFills(double price)
        {
            foreach (var z in _zones)
            {
                if (z.Filled) continue;
                if (z.IsBullish  && price <= z.Bottom) z.Filled = true;
                if (!z.IsBullish && price >= z.Top)    z.Filled = true;
            }
        }

        // =====================================================================
        //  Retest signal detection
        // =====================================================================
        private void CheckRetestSignals(double price)
        {
            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            int hourlyBar = CurrentBars[_hourlyBarsIdx];

            foreach (var z in _zones)
            {
                if (z.Filled || z.TradeTaken) continue;

                // Skip zones too far from current price
                double dist = Math.Min(Math.Abs(price - z.Bottom), Math.Abs(price - z.Top));
                if (dist > MaxZoneDistance) continue;

                bool inZone = price >= z.Bottom && price <= z.Top;

                if (!inZone)
                {
                    z.PriceWasOutside = true;
                    continue;
                }

                // Price just entered zone (was outside before)
                if (!z.PriceWasOutside)
                    continue;

                z.PriceWasOutside = false;

                if (z.IsBullish)
                {
                    // Bullish FVG retest → SHORT
                    EnterShort(ContractQuantity, "FVG_Short");
                }
                else
                {
                    // Bearish FVG retest → LONG
                    EnterLong(ContractQuantity, "FVG_Long");
                }

                z.TradeTaken     = true;
                z.TradeBarIndex  = hourlyBar;
                break; // one trade per tick
            }
        }

        // =====================================================================
        //  OnExecutionUpdate — submit OCO bracket after fill
        // =====================================================================
        protected override void OnExecutionUpdate(Execution execution, string executionId,
            double price, int quantity, MarketPosition marketPosition,
            string orderId, DateTime time)
        {
            if (execution.Order == null || execution.Order.OrderState != OrderState.Filled)
                return;

            string name = execution.Order.Name;

            if (name == "FVG_Long" || name == "FVG_Short")
            {
                double entry     = execution.Price;
                double slPrice   = name == "FVG_Long"
                    ? entry - StopLossTicks     * TickSize
                    : entry + StopLossTicks     * TickSize;
                double tpPrice   = name == "FVG_Long"
                    ? entry + ProfitTargetTicks * TickSize
                    : entry - ProfitTargetTicks * TickSize;

                _slOrder = null;
                _tpOrder = null;

                if (name == "FVG_Long")
                {
                    ExitLongStopMarket(0, true, ContractQuantity, slPrice, "SL", "FVG_Long");
                    ExitLongLimit     (0, true, ContractQuantity, tpPrice, "TP", "FVG_Long");
                }
                else
                {
                    ExitShortStopMarket(0, true, ContractQuantity, slPrice, "SL", "FVG_Short");
                    ExitShortLimit     (0, true, ContractQuantity, tpPrice, "TP", "FVG_Short");
                }

                _totalTrades++;
                Print($"[FILLED] {name} {quantity}x @ {entry:F2}  SL={slPrice:F2}  TP={tpPrice:F2}");
            }

            else if (name == "TP" || name == "SL")
            {
                // Cancel the orphaned OCO leg
                if (name == "TP" && _slOrder != null && _slOrder.OrderState == OrderState.Working)
                    CancelOrder(_slOrder);
                if (name == "SL" && _tpOrder != null && _tpOrder.OrderState == OrderState.Working)
                    CancelOrder(_tpOrder);

                // Track W/L
                if (name == "TP") _wins++;
                else               _losses++;

                Print($"[EXIT {name}] {quantity}x @ {execution.Price:F2}");
            }
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice,
            int quantity, int filled, double averageFillPrice, OrderState orderState,
            DateTime time, ErrorCode error, string comment)
        {
            if (order.Name == "SL" && orderState == OrderState.Working) _slOrder = order;
            if (order.Name == "TP" && orderState == OrderState.Working) _tpOrder = order;

            if (orderState == OrderState.Rejected)
                Print($"[ERROR] Order rejected: {order.Name} — {comment}");
        }

        protected override void OnPositionUpdate(Position position, double averagePrice,
            int quantity, MarketPosition marketPosition)
        {
            if (marketPosition == MarketPosition.Flat)
            {
                _slOrder = null;
                _tpOrder = null;
            }
        }

        // =====================================================================
        //  On-chart stats display
        // =====================================================================
        private void DrawStats(double price)
        {
            int activeZones  = _zones.Count(z => !z.Filled);
            int bullishZones = _zones.Count(z => !z.Filled && z.IsBullish);
            int bearishZones = _zones.Count(z => !z.Filled && !z.IsBullish);
            double wr        = _totalTrades > 0 ? (double)_wins / _totalTrades * 100.0 : 0.0;

            string text = string.Format(
                "FVG STRATEGY\n" +
                "────────────\n" +
                "Zones:   {0} ({1}↑ {2}↓)\n" +
                "Trades:  {3}  W:{4} L:{5}\n" +
                "Win Rate: {6:F1}%\n" +
                "────────────\n" +
                "SL: {7} ticks\n" +
                "TP: {8} ticks",
                activeZones, bearishZones, bullishZones,
                _totalTrades, _wins, _losses,
                wr,
                StopLossTicks, ProfitTargetTicks);

            Draw.TextFixed(this, "FVGStats", text, TextPosition.TopRight,
                Brushes.WhiteSmoke,
                new NinjaTrader.Gui.Tools.SimpleFont("Consolas", 12),
                Brushes.Transparent,
                Brushes.Black,
                80);
        }

        // =====================================================================
        //  Properties
        // =====================================================================

        #region FVG Detection

        [NinjaScriptProperty]
        [Range(1.0, 500.0)]
        [Display(Name = "Min Gap Size (Points)", GroupName = "1. FVG Detection", Order = 1,
                 Description = "Minimum FVG size in points to track.")]
        public double MinGapSizePoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10000)]
        [Display(Name = "Max Zone Age (Hourly Bars)", GroupName = "1. FVG Detection", Order = 2,
                 Description = "Discard zones older than this many hourly bars.")]
        public int MaxZoneAgeBars { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 2000.0)]
        [Display(Name = "Max Zone Distance (Points)", GroupName = "1. FVG Detection", Order = 3,
                 Description = "Ignore zones further than this many points from current price.")]
        public double MaxZoneDistance { get; set; }

        #endregion

        #region Execution

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Contract Quantity", GroupName = "2. Execution", Order = 1)]
        public int ContractQuantity { get; set; }

        [NinjaScriptProperty]
        [Range(1, 2000)]
        [Display(Name = "Stop Loss (Ticks)", GroupName = "2. Execution", Order = 2)]
        public int StopLossTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 2000)]
        [Display(Name = "Profit Target (Ticks)", GroupName = "2. Execution", Order = 3)]
        public int ProfitTargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Bars Cooldown (Hourly)", GroupName = "2. Execution", Order = 4,
                 Description = "Hourly bars to wait before re-trading the same zone.")]
        public int BarsCooldown { get; set; }

        #endregion

        #region Display

        [NinjaScriptProperty]
        [Display(Name = "Show Stats", GroupName = "3. Display", Order = 1)]
        public bool ShowStats { get; set; }

        #endregion
    }
}
