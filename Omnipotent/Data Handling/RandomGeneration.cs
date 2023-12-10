using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Omnipotent.Data_Handling
{
    public class RandomGeneration
    {
        public static string GenerateRandomLengthOfNumbers(int length)
        {
            Random rnd = new Random();
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < length; i++)
            {
                sb.Append(rnd.Next(0, 9));
            }
            return sb.ToString();
        }
    }
}
