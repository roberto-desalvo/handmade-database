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
            if (File.Exists(_path))
            {
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
    }
}
