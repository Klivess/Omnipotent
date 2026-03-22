using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Data;
using XGBoostSharp;
using static LLama.Native.NativeLibraryConfig;

namespace Omnipotent.Services.OmniTrader.Strategies
{
    public class TemplateStrategy : OmniTraderStrategy
    {
        public TemplateStrategy()
        {
            Name = "TemplateStrategy";
            Description = "A simple template strategy file.";
        }

        protected override async Task OnLoad()
        {
        }

        protected override async Task OnCandleClose(OmniTraderFinanceData.OHLCCandle latest)
        {
        }
    }
}
