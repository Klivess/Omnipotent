using Omnipotent.Services.OmniTrader.Data;
using Omnipotent.Services.OmniTrader.Backtesting;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System;
using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.OmniTrader
{
    public class OmniTraderSimulator
    {
        private OmniTrader parent;
        public OmniTraderSimulator(OmniTrader parent)
        {
            this.parent = parent;
        }
    }
}
