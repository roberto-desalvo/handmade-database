using RDS.Database.Console.Services.Models;
using RDS.Database.Console.Services.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RDS.Database.Console.Services
{
    public class Table
    {
        private readonly Pager _pager;
        public int FirstPageIndex { get; private set; }

        // Sto cachando l'indice dell'ultima pagina, vale solo finchè c'è una sola instanza 
        private int _lastPageIndex;

        public Table(int firstPageIndex, Pager pager)
        {
            if (firstPageIndex <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(firstPageIndex), "L'indice della prima pagina deve essere maggiore di zero.");
            }
            FirstPageIndex = firstPageIndex;
            _pager = pager ?? throw new ArgumentNullException(nameof(pager));
        }

        public RecordIdentifier Insert(ReadOnlySpan<byte> record)
        {
            Guard.ThrowIfOverMaxRecordLength(record.Length);

            var pageBuffer = new byte[Constants.PAGE_SIZE];

            if (_lastPageIndex <= 0)
            {
                var nextIndex = FirstPageIndex;

                while (nextIndex != 0)
                {
                    var page = Utils.GetPage(_pager, nextIndex, pageBuffer);
                    _lastPageIndex = nextIndex;
                    nextIndex = page.GetNextPageIndex();
                }
            }

            // Leggo l'ultima pagina
            var currentPage = Utils.GetPage(_pager, _lastPageIndex, pageBuffer);

            // Provo a inserire il record
            var slotIndex = currentPage.Insert(record);

            // Se riesco ritorno l'indice della pagina e lo slot
            if (slotIndex != -1)
            {
                _pager.Write(_lastPageIndex, currentPage.Data);
                return new RecordIdentifier(_lastPageIndex, slotIndex);
            }

            // Altrimenti creo una nuova pagina e inserisco il record lì
            var newPageIndex = _pager.AllocateNewPage();
            var newPage = new Page(new byte[Constants.PAGE_SIZE]);
            newPage.Init();
            currentPage.SetNextPageIndex(newPageIndex);
            slotIndex = newPage.Insert(record);

            if (slotIndex == -1)
            {
                throw new InvalidOperationException("Impossibile inserire il record nella nuova pagina.");
            }

            _pager.Write(_lastPageIndex, currentPage.Data);
            _pager.Write(newPageIndex, newPage.Data);
            _lastPageIndex = newPageIndex;


            return new RecordIdentifier(_lastPageIndex, slotIndex);
        }

        public ReadOnlySpan<byte> Read(RecordIdentifier rid)
        {
            Guard.ThrowIfLessThanOrEqualToZero(rid.PageIndex, nameof(rid.PageIndex));
            Guard.ThrowIfLessThanZero(rid.SlotIndex, nameof(rid.SlotIndex));

            var pageIndex = rid.PageIndex;
            var slotIndex = rid.SlotIndex;

            var page = Utils.GetPage(_pager, pageIndex);

            while (page.IsForwarding(slotIndex))
            {
                var newRid = page.ReadForwarding(slotIndex);
                page = Utils.GetPage(_pager, newRid.PageIndex);
                slotIndex = newRid.SlotIndex;
            }

            var record = page.Read(slotIndex);
            return record;
        }

        public void Delete(RecordIdentifier identifier)
        {
            var page = Utils.GetPage(_pager, identifier.PageIndex);
            page.Delete(identifier.SlotIndex);
            _pager.Write(identifier.PageIndex, page.Data);
        }

        public void Update(RecordIdentifier identifier, ReadOnlySpan<byte> record)
        {
            var page = Utils.GetPage(_pager, identifier.PageIndex);
            var updated = page.TryUpdate(identifier.SlotIndex, record);

            if (updated)
            {
                _pager.Write(identifier.PageIndex, page.Data);
                return;
            }

            var currentPageIndex = FirstPageIndex;
            var currentPage = Utils.GetPage(_pager, currentPageIndex);
            while (true)
            {
                // Provo a inserire nella prossima pagina
                var insertedIndex = currentPage.Insert(record);

                if (insertedIndex != -1)
                {
                    page.MarkAsForwarded(new RecordIdentifier(currentPageIndex, insertedIndex), identifier.SlotIndex);
                    _pager.Write(currentPageIndex, currentPage.Data);
                    _pager.Write(identifier.PageIndex, page.Data);
                    break;
                }

                // Se non riesco cambio pagina; se non c'è, ne alloco una nuova
                currentPageIndex = currentPage.GetNextPageIndex();

                if (currentPageIndex > 0)
                {
                    currentPage = Utils.GetPage(_pager, currentPageIndex);
                }
                else
                {
                    var newPageIndex = _pager.AllocateNewPage();
                    var newPage = new Page(new byte[Constants.PAGE_SIZE]);
                    newPage.Init();
                    currentPage.SetNextPageIndex(newPageIndex);
                    _pager.Write(currentPageIndex, currentPage.Data);

                    currentPage = newPage;
                    currentPageIndex = newPageIndex;
                    _lastPageIndex = newPageIndex;
                }
            }
        }

        public TableScanner Scan()
        {
            return new TableScanner(_pager, FirstPageIndex);
        }
    }
}
