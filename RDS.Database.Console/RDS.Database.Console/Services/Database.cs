using RDS.Database.Console.Services.Utilities;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RDS.Database.Console.Services
{
    public class Database : IDisposable
    {
        private readonly Pager _pager;
        public Table Table { get; private set; }

        public Database(string path)
        {
            _pager = new Pager(path);

            if (_pager.PagesCount == 0)
            {
                InitializeNewDatabase();
            }

            ValidateDatabase();
            Table = new Table(GetTableIndexFromFirstPage(), _pager);
        }

        private int GetTableIndexFromFirstPage()
        {
            var firstPageBuffer = new byte[Constants.PAGE_SIZE];
            _pager.Read(0, firstPageBuffer);
            return BinaryPrimitives.ReadInt32LittleEndian(firstPageBuffer.AsSpan(4, 4));
        }

        private void InitializeNewDatabase()
        {
            var firstPageIndex = _pager.AllocateNewPage();
            if (firstPageIndex != 0)
            {
                throw new InvalidOperationException("La prima pagina allocata non è la pagina 0");
            }

            // Scrivo la prima pagina con il magic number 

            // Nei primi 4 bytes della prima pagina c'è il magic number
            var firstPageBuffer = new byte[Constants.PAGE_SIZE];
            Constants.MAGIC_NUMBER.CopyTo(firstPageBuffer.AsSpan(0, 4));
                       

            // Scrivo la seconda pagina come pagina di dati vuota (è la prima pagina dell'unica tabella)
            var secondPageIndex = _pager.AllocateNewPage();
            var secondPageBuffer = new byte[Constants.PAGE_SIZE];
            var secondPage = new Page(secondPageBuffer);
            secondPage.Init(Constants.PAGE_TYPE_DATA);
            _pager.Write(secondPageIndex, secondPageBuffer);

            // Scrivo nei bytes 4-7 della prima pagina l'indice della tabella 
            BinaryPrimitives.WriteInt32LittleEndian(firstPageBuffer.AsSpan(4, 4), secondPageIndex);
            _pager.Write(firstPageIndex, firstPageBuffer);

            // Persisto su disco
            _pager.Sync();
        }

        private void ValidateDatabase()
        {
            var pageBuffer = new byte[Constants.PAGE_SIZE];
            _pager.Read(0, pageBuffer);
            var magicNumber = pageBuffer[..4];

            // Nei primi 4 bytes della prima pagina c'è il magic number
            if (!magicNumber.SequenceEqual(Constants.MAGIC_NUMBER))
            {
                throw new InvalidDataException("Il magic number della prima pagina non corrisponde a quello atteso.");
            }

            // Nei bytes 4-7 della prima pagina c'è l'indice della tabella
            var tableIndex = BinaryPrimitives.ReadInt32LittleEndian(pageBuffer.AsSpan(4, 4));

            if (tableIndex <= 0 || tableIndex >= _pager.PagesCount)
            {
                throw new InvalidDataException("L'indice della tabella nella prima pagina non è corretto.");
            }

            var tablePageBuffer = new byte[Constants.PAGE_SIZE];
            _pager.Read(tableIndex, tablePageBuffer);
            var tablePage = new Page(tablePageBuffer);
            tablePage.CheckInitialized();

            // TODO: controlla sia una pagina di dati quando avrai anche le pagine di indice
        }

        public void Sync()
        {
            _pager.Sync();
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            _pager.Dispose();
        }
    }
}
