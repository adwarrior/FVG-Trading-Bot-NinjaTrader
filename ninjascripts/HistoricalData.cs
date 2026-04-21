#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class HistoricalData : Strategy
    {
        private string filePath;
        private EMA ema21;
        private EMA ema75;
        private EMA ema150;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description         = @"Historical bar data feed for FVG detection — writes OHLC + EMAs on each bar close.";
                Name                = "HistoricalData";
                Calculate           = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                BarsRequiredToTrade = 20;

                HistoricalDataFilePath = @"C:\FVGBot\data\HistoricalData.csv";
            }
            else if (State == State.DataLoaded)
            {
                filePath = HistoricalDataFilePath;

                ema21  = EMA(21);
                ema75  = EMA(75);
                ema150 = EMA(150);

                // Write header once — prevents mid-session reload from wiping live data.
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                    if (!File.Exists(filePath))
                    {
                        using (StreamWriter w = new StreamWriter(filePath, false))
                            w.WriteLine("DateTime,Open,High,Low,Close,EMA21,EMA75,EMA150");
                    }
                }
                catch (Exception ex)
                {
                    Print($"HistoricalData init error: {ex.Message}");
                }
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade)
                return;

            try
            {
                using (StreamWriter w = new StreamWriter(filePath, true))
                {
                    w.WriteLine(
                        $"{Time[0]:yyyy-MM-dd HH:mm:ss},{Open[0]:F2},{High[0]:F2},{Low[0]:F2},{Close[0]:F2}," +
                        $"{ema21[0]:F2},{ema75[0]:F2},{ema150[0]:F2}");
                }
            }
            catch (Exception ex)
            {
                Print($"HistoricalData write error: {ex.Message}");
            }
        }

        #region Properties

        [NinjaScriptProperty]
        [Display(Name = "Historical Data File Path", GroupName = "HistoricalData Parameters", Order = 1,
                 Description = "File that Python reads for bar history and indicator values.")]
        public string HistoricalDataFilePath { get; set; }

        #endregion
    }
}
