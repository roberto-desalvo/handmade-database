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
        public void Table_Constructor_WithInvalidFirstPageIndex_ShouldThrow()
        {
            using var pager = new Pager(_path);
            pager.AllocateNewPage();

            Assert.Throws<ArgumentOutOfRangeException>(() => new Table(0, pager));
            Assert.Throws<ArgumentOutOfRangeException>(() => new Table(-1, pager));
        }

        [Fact]
        public void Table_Constructor_WithNullPager_ShouldThrow()
        {
            Assert.Throws<ArgumentNullException>(() => new Table(1, null!));
        }

        [Fact]
        public void Table_Update_WhenNewSizeStillFitsInSamePage_ShouldUpdateAndRemainReadable()
        {
            using var pager = new Pager(_path);
            pager.AllocateNewPage(); // pagina 0 riservata all'header

            var firstPageIndex = pager.AllocateNewPage();
            var firstPage = new Page(new byte[Constants.PAGE_SIZE]);
            firstPage.Init();
            pager.Write(firstPageIndex, firstPage.Data);

            var table = new Table(firstPageIndex, pager);
            var rid = table.Insert(BuildRecord(1, 10));

            var updated = BuildRecord(1, 20);
            table.Update(rid, updated);

            Assert.Equal(updated, table.Read(rid).ToArray());
        }

        [Fact]
        public void Table_Update_WhenTooBigForOriginalPage_ShouldForwardToExistingNextPage()
        {
            using var pager = new Pager(_path);
            pager.AllocateNewPage(); // pagina 0 riservata all'header

            var firstPageIndex = pager.AllocateNewPage();
            var secondPageIndex = pager.AllocateNewPage();

            // Riempio completamente la prima pagina lasciando spazio solo per un record noto,
            // usando lo stesso schema di riempimento usato in PageTests.
            var firstBuffer = new byte[Constants.PAGE_SIZE];
            var firstPage = new Page(firstBuffer);
            firstPage.Init();

            var iterazioni = 100;
            var toInsertSize = 10;
            for (int i = 0; i < iterazioni; i++)
            {
                firstPage.Insert(new byte[toInsertSize]);
            }

            var remaining = Constants.PAGE_SIZE - Constants.PAGE_HEADER_SIZE - (iterazioni * toInsertSize) - (iterazioni * Constants.PAGE_SLOT_SIZE) - Constants.PAGE_SLOT_SIZE;
            var targetRecord = BuildRecord(999, remaining - 4);
            var targetIndex = firstPage.Insert(targetRecord);

            // La prima pagina ha già un collegamento a una seconda pagina, esistente e vuota.
            firstPage.SetNextPageIndex(secondPageIndex);
            pager.Write(firstPageIndex, firstBuffer);

            var secondBuffer = new byte[Constants.PAGE_SIZE];
            var secondPage = new Page(secondBuffer);
            secondPage.Init();
            pager.Write(secondPageIndex, secondBuffer);

            var table = new Table(firstPageIndex, pager);
            var rid = new RecordIdentifier(firstPageIndex, targetIndex);

            // Un record più grande del record esistente (che occupa già tutto lo spazio della
            // pagina, ormai piena) forza il forwarding verso la seconda pagina, già esistente.
            var biggerRecord = BuildRecord(999, remaining - 4 + 500);
            table.Update(rid, biggerRecord);

            Assert.Equal(biggerRecord, table.Read(rid).ToArray());

            // Lo slot originale deve ora risultare marcato come forwarding.
            var rawFirstBuffer = new byte[Constants.PAGE_SIZE];
            pager.Read(firstPageIndex, rawFirstBuffer);
            var rawFirstPage = new Page(rawFirstBuffer);
            Assert.True(rawFirstPage.IsForwarding(targetIndex));
        }

        // BUG NOTO (non corretto su richiesta): quando l'update di un record richiede il
        // forwarding verso una NUOVA pagina (nessuna pagina successiva già esistente), Table.Update
        // scrive erroneamente la pagina aggiornata sull'indice `_lastPageIndex` invece che su
        // `currentPageIndex`. Se `_lastPageIndex` non è mai stato impostato (valore di default 0,
        // cioè subito dopo l'apertura del database senza mai chiamare Insert), questo sovrascrive
        // la pagina di header del database (pagina 0), corrompendola. Questo test documenta e
        // riproduce la corruzione: vedi Services/Table.cs, branch di allocazione nuova pagina in Update.
        [Fact]
        public void Table_Update_WhenForwardingNeedsNewPageAndLastPageIndexIsStale_KnownBug_CorruptsWrongPage()
        {
            using var pager = new Pager(_path);
            var headerPageIndex = pager.AllocateNewPage();
            Assert.Equal(0, headerPageIndex);

            // Scrivo un header fittizio con un magic number riconoscibile, per poter verificare
            // in seguito che sia stato sovrascritto.
            var headerBuffer = new byte[Constants.PAGE_SIZE];
            Constants.MAGIC_NUMBER.CopyTo(headerBuffer.AsSpan(0, 4));
            pager.Write(headerPageIndex, headerBuffer);

            var firstPageIndex = pager.AllocateNewPage();
            var firstBuffer = new byte[Constants.PAGE_SIZE];
            var firstPage = new Page(firstBuffer);
            firstPage.Init();

            // Riempio completamente la pagina: nessuno spazio residuo, nessuna pagina successiva.
            var iterazioni = 100;
            var toInsertSize = 10;
            for (int i = 0; i < iterazioni; i++)
            {
                firstPage.Insert(new byte[toInsertSize]);
            }

            var remaining = Constants.PAGE_SIZE - Constants.PAGE_HEADER_SIZE - (iterazioni * toInsertSize) - (iterazioni * Constants.PAGE_SLOT_SIZE) - Constants.PAGE_SLOT_SIZE;
            var targetIndex = firstPage.Insert(new byte[remaining]);
            pager.Write(firstPageIndex, firstBuffer);

            // Creo una NUOVA istanza di Table senza mai chiamare Insert: il campo privato
            // _lastPageIndex resta al suo valore di default (0), cioè l'indice della pagina di header.
            var table = new Table(firstPageIndex, pager);
            var rid = new RecordIdentifier(firstPageIndex, targetIndex);

            // L'update richiede più spazio di quanto la pagina possa offrire e non c'è una pagina
            // successiva: Table.Update alloca una nuova pagina per il forwarding.
            table.Update(rid, new byte[remaining + 1]);

            var afterHeaderBuffer = new byte[Constants.PAGE_SIZE];
            pager.Read(headerPageIndex, afterHeaderBuffer);

            // Il magic number dell'header, integro prima dell'update, risulta ora sovrascritto:
            // questo dimostra la corruzione causata dal bug.
            Assert.False(afterHeaderBuffer.AsSpan(0, 4).SequenceEqual(Constants.MAGIC_NUMBER));
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
