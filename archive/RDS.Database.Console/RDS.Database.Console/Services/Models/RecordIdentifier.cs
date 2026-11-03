using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RDS.Database.Console.Services.Models
{
    public struct RecordIdentifier(int pageIndex, int slotIndex)
    {
        public int PageIndex { get; private set; } = pageIndex;
        public int SlotIndex { get; private set; } = slotIndex;
    }
}
