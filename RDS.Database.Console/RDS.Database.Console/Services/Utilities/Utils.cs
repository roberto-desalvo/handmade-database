using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RDS.Database.Console.Services.Utilities
{
    public static class Utils
    {
        public static Page GetPage(Pager pager, int pageIndex)
        {
            var pageBuffer = new byte[Constants.PAGE_SIZE];
            return GetPage(pager, pageIndex, pageBuffer);
        }

        public static Page GetPage(Pager pager, int pageIndex, Span<byte> pageBuffer)
        {
            pager.Read(pageIndex, pageBuffer);
            return new Page(pageBuffer);
        }
    }
}
