using System;
using System.Collections.Generic;
using System.Text;
using RDS.Database.Console.Services;
using RDS.Database.Console.Services.Utilities;

namespace RDS.Database.Tests
{
    public class PagerTests : IDisposable
    {
        private readonly string _path;

        public PagerTests()
        {
            _path = Path.Combine(Path.GetTempPath(), $"pager_tests_{Guid.NewGuid()}.db");
        }

        public void Dispose()
        {
            if (!File.Exists(_path))
            {
                return;
            }

            try
            {
                File.Delete(_path);
            }
            catch (IOException)
            {
                // Alcuni test provocano un'eccezione nel costruttore di Pager dopo che l'handle sul
                // file è già stato aperto: non essendoci un oggetto da disporre, l'handle viene
                // rilasciato solo alla finalizzazione. Forziamo la GC per poter cancellare il file.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                File.Delete(_path);
            }
        }

        // Se leggo una pagina che non esiste, dovrebbe lanciare un'eccezione
        [Fact]
        public void Read_WhenPageDoesNotExist_ShouldThrow()
        {
            using var pager = new Pager(_path);
            var buffer = new byte[Constants.PAGE_SIZE];

            Assert.Throws<ArgumentException>(() => pager.Read(0, buffer));
        }

        // Se scrivo una pagina che non esiste, dovrebbe lanciare un'eccezione
        [Fact]
        public void Write_WhenPageDoesNotExist_ShouldThrow()
        {
            using var pager = new Pager(_path);
            var buffer = new byte[Constants.PAGE_SIZE];

            Assert.Throws<ArgumentException>(() => pager.Write(0, buffer));
        }

        // Se leggo una pagina che esiste, dovrebbe leggere correttamente i dati
        [Fact]
        public void Read_WhenPageExists_ShouldReadData()
        {
            using var pager = new Pager(_path);
            var pageIndex = pager.AllocateNewPage();

            var buffer = new byte[Constants.PAGE_SIZE];
            pager.Read(pageIndex, buffer);

            Assert.Equal(new byte[Constants.PAGE_SIZE], buffer);
        }

        // Se scrivo una pagina che esiste, dovrebbe scrivere correttamente i dati
        [Fact]
        public void Write_WhenPageExists_ShouldWriteData()
        {
            using var pager = new Pager(_path);
            var pageIndex = pager.AllocateNewPage();

            var toWrite = new byte[Constants.PAGE_SIZE];
            toWrite[0] = 42;

            pager.Write(pageIndex, toWrite);

            var buffer = new byte[Constants.PAGE_SIZE];
            pager.Read(pageIndex, buffer);

            Assert.Equal(toWrite, buffer);
        }

        // Se scrivo una pagina che esiste e poi la leggo, i dati dovrebbero essere gli stessi
        [Fact]
        public void WriteThenRead_ShouldReturnSameData()
        {
            using var pager = new Pager(_path);
            var pageIndex = pager.AllocateNewPage();

            var toWrite = new byte[Constants.PAGE_SIZE];
            new Random(42).NextBytes(toWrite);

            pager.Write(pageIndex, toWrite);

            var read = new byte[Constants.PAGE_SIZE];
            pager.Read(pageIndex, read);

            Assert.Equal(toWrite, read);
        }

        // Se chiudo e riapro il file, i dati dovrebbero essere gli stessi
        [Fact]
        public void CloseAndReopenFile_ShouldPreserveData()
        {
            var toWrite = new byte[Constants.PAGE_SIZE];
            new Random(1234).NextBytes(toWrite);

            int pageIndex;
            using (var pager = new Pager(_path))
            {
                pageIndex = pager.AllocateNewPage();
                pager.Write(pageIndex, toWrite);
                pager.Sync();
            }

            using (var pager = new Pager(_path))
            {
                var read = new byte[Constants.PAGE_SIZE];
                pager.Read(pageIndex, read);

                Assert.Equal(toWrite, read);
            }
        }

        [Fact]
        public void Constructor_WithNullOrEmptyPath_ShouldThrow()
        {
            Assert.Throws<ArgumentNullException>(() => new Pager(null!));
            Assert.Throws<ArgumentNullException>(() => new Pager(string.Empty));
            Assert.Throws<ArgumentNullException>(() => new Pager("   "));
        }

        [Fact]
        public void Constructor_WhenFileSizeIsNotMultipleOfPageSize_ShouldThrow()
        {
            File.WriteAllBytes(_path, new byte[100]);

            Assert.Throws<IOException>(() => new Pager(_path));
        }

        [Fact]
        public void AllocateNewPage_ShouldReturnSequentialIndicesAndIncreasePagesCount()
        {
            using var pager = new Pager(_path);

            Assert.Equal(0, pager.PagesCount);

            var first = pager.AllocateNewPage();
            Assert.Equal(0, first);
            Assert.Equal(1, pager.PagesCount);

            var second = pager.AllocateNewPage();
            Assert.Equal(1, second);
            Assert.Equal(2, pager.PagesCount);
        }

        [Fact]
        public void Read_WithBufferOfWrongSize_ShouldThrow()
        {
            using var pager = new Pager(_path);
            pager.AllocateNewPage();

            Assert.Throws<ArgumentException>(() => pager.Read(0, new byte[10]));
        }

        [Fact]
        public void Write_WithBufferOfWrongSize_ShouldThrow()
        {
            using var pager = new Pager(_path);
            pager.AllocateNewPage();

            Assert.Throws<ArgumentException>(() => pager.Write(0, new byte[10]));
        }
    }
}
