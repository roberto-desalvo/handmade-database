using RDS.Database.Console.Services;
using RDS.Database.Console.Services.Models;
using RDS.Database.Console.Services.Utilities;

namespace RDS.Database.Tests
{
    // Test dedicati alla funzionalità di compattazione della pagina (Compact/TryCompact) e a come
    // viene sfruttata da Insert, TryUpdate e MarkAsForwarded per recuperare spazio frammentato da
    // record cancellati, quando lo spazio libero CONTIGUO non basterebbe da solo.
    public class PageCompactionTests
    {
        // Costruisce un record riconoscibile: ogni byte = (seed + indice), utile per verificare che
        // il contenuto sopravviva intatto a una compattazione (non solo la dimensione).
        private static byte[] BuildPattern(byte seed, int length)
        {
            var data = new byte[length];
            for (int i = 0; i < length; i++)
            {
                data[i] = (byte)(seed + i);
            }
            return data;
        }

        [Fact]
        public void Insert_WhenContiguousSpaceIsZeroButDeletedRecordsFreeEnoughTotalSpace_ShouldSucceedViaCompaction()
        {
            var page = new Page(new byte[4096]);
            page.Init();

            var iterazioni = 100;
            var toInsertSize = 10;
            var fillerIndices = new List<int>();

            for (int i = 0; i < iterazioni; i++)
            {
                fillerIndices.Add(page.Insert(BuildPattern((byte)i, toInsertSize)));
            }

            // Riempio esattamente lo spazio residuo: la pagina resta a zero byte liberi (stesso
            // schema usato in PageTests per il caso "nessuno spazio").
            var exactFillSize = Constants.PAGE_SIZE - Constants.PAGE_HEADER_SIZE - (iterazioni * toInsertSize) - (iterazioni * Constants.PAGE_SLOT_SIZE) - Constants.PAGE_SLOT_SIZE;
            var bigContent = BuildPattern(200, exactFillSize);
            var bigIndex = page.Insert(bigContent);

            // Nessuno spazio contiguo: anche un solo byte non entra più.
            Assert.Equal(-1, page.Insert(new byte[1]));

            // Cancello 3 dei filler iniziali. Questi occupano la parte più "alta" della pagina
            // (sono stati inseriti per primi), quindi lo spazio che liberano NON è adiacente al
            // confine di spazio libero attuale: è frammentato, recuperabile solo compattando.
            page.Delete(fillerIndices[0]);
            page.Delete(fillerIndices[1]);
            page.Delete(fillerIndices[2]);

            // 30 byte liberati in totale (3 x 10). Chiedendone 27 (+4 di slot = 31) non deve
            // bastare nemmeno con la compattazione.
            Assert.Equal(-1, page.Insert(BuildPattern(210, 27)));

            // Chiedendone 26 (+4 di slot = 30) deve entrare esattamente, ma solo grazie alla
            // compattazione (senza, lo spazio contiguo sarebbe ancora zero).
            var reclaimedContent = BuildPattern(220, 26);
            var reclaimedIndex = page.Insert(reclaimedContent);

            Assert.True(reclaimedIndex >= 0);
            Assert.Equal(reclaimedContent, page.Read(reclaimedIndex).ToArray());

            // I record cancellati devono restare cancellati dopo la compattazione...
            Assert.True(page.IsDeleted(fillerIndices[0]));
            Assert.True(page.IsDeleted(fillerIndices[1]));
            Assert.True(page.IsDeleted(fillerIndices[2]));

            // ...e tutti gli altri record vivi devono sopravvivere intatti, con lo stesso indice e
            // lo stesso contenuto di prima.
            for (int i = 3; i < iterazioni; i++)
            {
                Assert.Equal(BuildPattern((byte)i, toInsertSize), page.Read(fillerIndices[i]).ToArray());
            }
            Assert.Equal(bigContent, page.Read(bigIndex).ToArray());
        }

        [Fact]
        public void TryUpdate_WhenContiguousSpaceIsNotEnoughButDeletedRecordFreesEnoughTotalSpace_ShouldGrowViaCompaction()
        {
            var page = new Page(new byte[4096]);
            page.Init();

            // Riempio la pagina lasciando solo 3 byte contigui liberi dopo il record bersaglio.
            var fillerSize = Constants.PAGE_SIZE - Constants.PAGE_HEADER_SIZE - (2 * Constants.PAGE_SLOT_SIZE) - 8 - 3;
            var fillerContent = BuildPattern(1, fillerSize);
            var fillerIndex = page.Insert(fillerContent);

            var targetContent = BuildPattern(50, 8);
            var targetIndex = page.Insert(targetContent);

            // Senza compattazione, un aggiornamento a 4000 byte non può funzionare: lo spazio
            // contiguo libero è di soli 3 byte e il filler è ancora vivo.
            Assert.False(page.TryUpdate(targetIndex, new byte[4000]));
            Assert.Equal(targetContent, page.Read(targetIndex).ToArray());

            // Cancello il filler: libera ~4074 byte, ma non adiacenti al confine di spazio libero
            // attuale (il filler è stato inserito per primo, quindi occupa la parte più "alta").
            page.Delete(fillerIndex);

            var biggerContent = BuildPattern(60, 4000);
            var success = page.TryUpdate(targetIndex, biggerContent);

            Assert.True(success);
            Assert.Equal(biggerContent, page.Read(targetIndex).ToArray());

            // Il filler cancellato deve restare cancellato dopo la compattazione.
            Assert.True(page.IsDeleted(fillerIndex));
        }

        [Fact]
        public void MarkAsForwarded_WhenRecordTooSmallButDeletedRecordFreesEnoughSpace_ShouldSucceedAndPreserveRid()
        {
            var page = new Page(new byte[4096]);
            page.Init();

            // Record troppo piccolo per contenere un RID di forwarding (6 byte).
            var targetIndex = page.Insert(new byte[] { 1, 2, 3 });

            // Riempio quasi tutto il resto della pagina, lasciando solo 2 byte contigui liberi:
            // non bastano nemmeno per il RID (6 byte) senza compattare.
            var fillerSize = Constants.PAGE_SIZE - Constants.PAGE_HEADER_SIZE - (2 * Constants.PAGE_SLOT_SIZE) - 3 - 2;
            var fillerIndex = page.Insert(BuildPattern(9, fillerSize));

            page.Delete(fillerIndex);

            var forwardingTarget = new RecordIdentifier(7, 2);
            page.MarkAsForwarded(forwardingTarget, targetIndex);

            Assert.True(page.IsForwarding(targetIndex));

            var rid = page.ReadForwarding(targetIndex);
            Assert.Equal(forwardingTarget.PageIndex, rid.PageIndex);
            Assert.Equal(forwardingTarget.SlotIndex, rid.SlotIndex);

            // Il record è marcato come migrato: leggerlo direttamente deve segnalarlo, non
            // restituire i vecchi byte o quelli del RID.
            ApplicationException? readThrown = null;
            try
            {
                page.Read(targetIndex);
            }
            catch (ApplicationException ex)
            {
                readThrown = ex;
            }
            Assert.NotNull(readThrown);

            // Lo spazio usato dal RID deve risultare correttamente "riservato": un inserimento
            // successivo non deve poter sovrascrivere il puntatore appena scritto.
            var afterInsertIndex = page.Insert(new byte[] { 42, 42, 42, 42, 42, 42, 42, 42, 42, 42 });
            Assert.True(afterInsertIndex >= 0);

            var ridAfterFurtherInsert = page.ReadForwarding(targetIndex);
            Assert.Equal(forwardingTarget.PageIndex, ridAfterFurtherInsert.PageIndex);
            Assert.Equal(forwardingTarget.SlotIndex, ridAfterFurtherInsert.SlotIndex);
        }

        [Fact]
        public void MarkAsForwarded_WhenRecordTooSmallAndNoSpaceEvenAfterCompaction_ShouldThrowWithoutCorruptingRecord()
        {
            var page = new Page(new byte[4096]);
            page.Init();

            var originalContent = new byte[] { 1, 2, 3 };
            var targetIndex = page.Insert(originalContent);

            // Riempio esattamente il resto della pagina: zero byte liberi, senza alcun record
            // cancellato da recuperare. Compattare non può aiutare in questo caso.
            var fillerSize = Constants.PAGE_SIZE - Constants.PAGE_HEADER_SIZE - (2 * Constants.PAGE_SLOT_SIZE) - originalContent.Length;
            page.Insert(new byte[fillerSize]);

            InvalidOperationException? thrown = null;
            try
            {
                page.MarkAsForwarded(new RecordIdentifier(1, 0), targetIndex);
            }
            catch (InvalidOperationException ex)
            {
                thrown = ex;
            }
            Assert.NotNull(thrown);

            // Il record originale non deve essere stato toccato dal tentativo fallito.
            Assert.False(page.IsForwarding(targetIndex));
            Assert.False(page.IsDeleted(targetIndex));
            Assert.Equal(originalContent, page.Read(targetIndex).ToArray());
        }

        [Fact]
        public void Insert_AfterCompaction_StillRequiresSpaceForNewSlotNotJustForData()
        {
            var page = new Page(new byte[4096]);
            page.Init();

            // Un solo record che occupa quasi tutta la pagina, cancellato subito dopo: la
            // compattazione può liberare tutto il suo spazio.
            var fillerSize = Constants.PAGE_SIZE - Constants.PAGE_HEADER_SIZE - Constants.PAGE_SLOT_SIZE;
            var fillerIndex = page.Insert(new byte[fillerSize]);
            page.Delete(fillerIndex);

            // Lo spazio totale libero, dopo la cancellazione, è esattamente fillerSize (il record
            // cancellato non conta più, ma lo slot che occupava resta comunque nella directory).
            // Chiedere esattamente fillerSize byte di dati non deve entrare: servirebbe anche lo
            // spazio per il nuovo slot (4 byte in più).
            Assert.Equal(-1, page.Insert(new byte[fillerSize]));

            // Chiedendo 4 byte in meno, invece, deve esserci spazio esatto anche per il nuovo slot.
            var content = BuildPattern(5, fillerSize - Constants.PAGE_SLOT_SIZE);
            var insertedIndex = page.Insert(content);

            Assert.True(insertedIndex >= 0);
            Assert.Equal(content, page.Read(insertedIndex).ToArray());
        }
    }
}
