using RDS.Database.Console.Services.Models;
using RDS.Database.Console.Services.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RDS.Database.Console.Services
{
    public ref struct TableScanner
    {
        private readonly Pager _pager;

        private int _currentPageIndex;
        private int _currentSlotIndex;
        private Page _currentPage;
        private int _currentPageRecordCount;
        public readonly RecordIdentifier CurrentPosition => new(_currentPageIndex, _currentSlotIndex);
        public readonly ReadOnlySpan<byte> Current => GetCurrent();

        private readonly ReadOnlySpan<byte> GetCurrent()
        {
            if (_currentSlotIndex < 0 || _currentSlotIndex >= _currentPageRecordCount)
            {
                throw new InvalidOperationException("La posizione corrente non è valida.");
            }
            return _currentPage.Read(_currentSlotIndex);
        }


        public TableScanner(Pager pager, int firstPageIndex)
        {
            _pager = pager ?? throw new ArgumentNullException(nameof(pager));
            _currentPageIndex = firstPageIndex;
            _currentSlotIndex = -1;
            _currentPage = Utils.GetPage(_pager, _currentPageIndex);
            _currentPageRecordCount = _currentPage.GetRecordCount();
        }

        public bool MoveNext()
        {
            while (true)
            {
                _currentSlotIndex++;

                // Così skippo anche le pagine vuote
                while (_currentSlotIndex >= _currentPageRecordCount) 
                {
                    var nextPageIndex = _currentPage.GetNextPageIndex();
                    if (nextPageIndex == 0)
                    {
                        // Lo lascio sull'ultima posizione valida, così che CurrentPosition sia coerente con Current
                        _currentSlotIndex--;
                        return false;
                    }

                    _currentPageIndex = nextPageIndex;
                    _currentPage = Utils.GetPage(_pager, _currentPageIndex);
                    _currentPageRecordCount = _currentPage.GetRecordCount();
                    _currentSlotIndex = 0;
                }

                // I forwarding li skipppo e verranno incontrati dopo (ordine non garantito)
                if (!_currentPage.IsDeleted(_currentSlotIndex) && !_currentPage.IsForwarding(_currentSlotIndex))
                {
                    return true;
                }
            }
        }
    }
}
