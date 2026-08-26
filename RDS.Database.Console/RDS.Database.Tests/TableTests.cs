using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using RDS.Database.Console.Services;
using RDS.Database.Console.Services.Models;
using RDS.Database.Console.Services.Utilities;
using Xunit;

namespace RDS.Database.Tests
{
    public class TableTests : IDisposable
    {
        private readonly string _path;
        private readonly Random _random = new Random(12345);

        public TableTests()
        {
            _path = Path.Combine(Path.GetTempPath(), $"table_tests_{Guid.NewGuid()}.db");
        }

        public void Dispose()
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }

        // Crea un record riconoscibile: primi 4 byte = indice progressivo, il resto è riempimento
        // deterministico (dipendente dall'indice) di lunghezza variabile.
        private static byte[] BuildRecord(int recordId, int paddingLength)
        {
            var record = new byte[4 + paddingLength];
            BinaryPrimitives.WriteInt32LittleEndian(record, recordId);

            for (int i = 0; i < paddingLength; i++)
            {
                record[4 + i] = (byte)(recordId + i);
            }

            return record;
        }

        [Fact]
        public void Table_InsertSyncReopenScanReadEarlyExit_ShouldPreserveDataAndPositions()
        {
            int firstPageIndex;
            var inserted = new List<(int RecordId, RecordIdentifier Position, byte[] Content)>();

            using (var pager = new Pager(_path))
            {
                // La pagina 0 è riservata all'header del database, quindi la prima pagina della
                // tabella deve essere allocata dopo.
                pager.AllocateNewPage();

                firstPageIndex = pager.AllocateNewPage();
                var firstPage = new Page(new byte[Constants.PAGE_SIZE]);
                firstPage.Init();
                pager.Write(firstPageIndex, firstPage.Data);

                var table = new Table(firstPageIndex, pager);

                // Inserisco record di lunghezza variabile finché non ho almeno 4 pagine di dati.
                var recordId = 0;
                while (pager.PagesCount < firstPageIndex + 4)
                {
                    var paddingLength = _random.Next(1, 200);
                    var content = BuildRecord(recordId, paddingLength);

                    var position = table.Insert(content);
                    inserted.Add((recordId, position, content));

                    recordId++;
                }

                pager.Sync();
            }

            // Il primo record inserito deve trovarsi nella prima pagina, primo slot.
            Assert.Equal(new RecordIdentifier(firstPageIndex, 0), inserted[0].Position);

            // Riapro il file con un nuovo pager e una nuova tabella sullo stesso path e stesso
            // indice di prima pagina.
            using (var pager = new Pager(_path))
            {
                var table = new Table(firstPageIndex, pager);

                // Scan completo: stesso numero di record, stesso ordine, stessi byte, stesse posizioni.
                var scanner = table.Scan();
                var scannedCount = 0;

                while (scanner.MoveNext())
                {
                    var expected = inserted[scannedCount];

                    Assert.Equal(expected.Position, scanner.CurrentPosition);
                    Assert.Equal(expected.Content, scanner.Current.ToArray());

                    scannedCount++;
                }

                Assert.Equal(inserted.Count, scannedCount);

                // Read puntuale su una manciata di RecordId sparsi, incluso (firstPage, 0) e l'ultimo inserito.
                var scatteredIndices = new[]
                {
                    0,
                    inserted.Count / 4,
                    inserted.Count / 2,
                    (inserted.Count * 3) / 4,
                    inserted.Count - 1
                }.Distinct();

                foreach (var index in scatteredIndices)
                {
                    var expected = inserted[index];
                    var read = table.Read(new RecordIdentifier(expected.Position.PageIndex, expected.Position.SlotIndex));

                    Assert.Equal(expected.Content, read.ToArray());
                }

                Assert.Equal(new RecordIdentifier(firstPageIndex, 0), inserted[0].Position);

                // Early exit: interrompo uno scan a metà con un break, senza eccezioni, e lascio
                // che il cursore venga abbandonato senza conseguenze.
                var partialScanner = table.Scan();
                var partialCount = 0;
                var halfway = inserted.Count / 2;

                while (partialScanner.MoveNext())
                {
                    partialCount++;
                    if (partialCount == halfway)
                    {
                        break;
                    }
                }

                Assert.Equal(halfway, partialCount);

                // La tabella deve continuare a funzionare correttamente dopo l'abbandono del cursore.
                var rescanCount = 0;
                var rescanner = table.Scan();
                while (rescanner.MoveNext())
                {
                    rescanCount++;
                }

                Assert.Equal(inserted.Count, rescanCount);
            }
        }

        [Fact]
        public void Table_DeleteSomeRecords_ScanShouldReturnOnlyRemainingInCorrectOrder()
        {
            int firstPageIndex;
            var inserted = new List<(int RecordId, RecordIdentifier Position, byte[] Content)>();

            using (var pager = new Pager(_path))
            {
                // La pagina 0 è riservata all'header del database.
                pager.AllocateNewPage();

                firstPageIndex = pager.AllocateNewPage();
                var firstPage = new Page(new byte[Constants.PAGE_SIZE]);
                firstPage.Init();
                pager.Write(firstPageIndex, firstPage.Data);

                var table = new Table(firstPageIndex, pager);

                // Inserisco record di lunghezza variabile finché non ho almeno 3 pagine di dati,
                // così da cancellare record sia a metà pagina che a cavallo tra pagine diverse.
                var recordId = 0;
                while (pager.PagesCount < firstPageIndex + 3)
                {
                    var paddingLength = _random.Next(1, 200);
                    var content = BuildRecord(recordId, paddingLength);

                    var position = table.Insert(content);
                    inserted.Add((recordId, position, content));

                    recordId++;
                }

                // Cancello alcuni record sparsi: il primo, uno a un terzo, uno a metà e l'ultimo.
                var toDeleteIndices = new[]
                {
                    0,
                    inserted.Count / 3,
                    inserted.Count / 2,
                    inserted.Count - 1
                }.Distinct().ToList();

                foreach (var index in toDeleteIndices)
                {
                    table.Delete(inserted[index].Position);
                }

                var expectedRemaining = inserted
                    .Where((_, index) => !toDeleteIndices.Contains(index))
                    .ToList();

                // Lo scan deve saltare i record cancellati e restituire solo quelli rimanenti,
                // nello stesso ordine in cui sono stati inseriti.
                var scanner = table.Scan();
                var scannedCount = 0;

                while (scanner.MoveNext())
                {
                    var expected = expectedRemaining[scannedCount];

                    Assert.Equal(expected.Position, scanner.CurrentPosition);
                    Assert.Equal(expected.Content, scanner.Current.ToArray());

                    scannedCount++;
                }

                Assert.Equal(expectedRemaining.Count, scannedCount);

                pager.Sync();
            }
        }
    }
}
