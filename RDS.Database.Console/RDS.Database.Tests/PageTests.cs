using RDS.Database.Console.Services;
using RDS.Database.Console.Services.Models;
using RDS.Database.Console.Services.Utilities;

namespace RDS.Database.Tests
{
    public class PageTests
    {
        [Fact]
        public void Page_GetRecordCount_WhenPageIsNotInitialized_ShouldThrow()
        {
            var page = new Page(new byte[4096]);

            InvalidOperationException? thrown = null;
            try
            {
                page.GetRecordCount();
            }
            catch (InvalidOperationException ex)
            {
                thrown = ex;
            }
            Assert.NotNull(thrown);
        }

        [Fact]
        public void Page_Read_WhenIndexIsNegativeOrOutOfRange_ShouldThrow()
        {
            var page = new Page(new byte[4096]);
            page.Init();
            page.Insert(new byte[] { 1 });

            ArgumentOutOfRangeException? thrownForNegative = null;
            try
            {
                page.Read(-1);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                thrownForNegative = ex;
            }
            Assert.NotNull(thrownForNegative);

            ArgumentOutOfRangeException? thrownForOutOfRange = null;
            try
            {
                page.Read(1);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                thrownForOutOfRange = ex;
            }
            Assert.NotNull(thrownForOutOfRange);
        }

        [Fact]
        public void Page_NextPageIndex_WhenNotSet_DefaultsToZero_AndCanBeSetAndRead()
        {
            var page = new Page(new byte[4096]);
            page.Init();

            Assert.Equal(0, page.GetNextPageIndex());

            page.SetNextPageIndex(7);

            Assert.Equal(7, page.GetNextPageIndex());
        }

        [Fact]
        public void Page_SetNextPageIndex_WhenZeroOrNegative_ShouldThrow()
        {
            var page = new Page(new byte[4096]);
            page.Init();

            ArgumentOutOfRangeException? thrownForZero = null;
            try
            {
                page.SetNextPageIndex(0);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                thrownForZero = ex;
            }
            Assert.NotNull(thrownForZero);

            ArgumentOutOfRangeException? thrownForNegative = null;
            try
            {
                page.SetNextPageIndex(-1);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                thrownForNegative = ex;
            }
            Assert.NotNull(thrownForNegative);
        }

        [Fact]
        public void Page_Forwarding_MarkReadAndDetect_ShouldWorkCorrectly()
        {
            var page = new Page(new byte[4096]);
            page.Init();

            var insertedIndex = page.Insert(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

            Assert.False(page.IsForwarding(insertedIndex));

            page.MarkAsForwarded(new RecordIdentifier(5, 2), insertedIndex);

            Assert.True(page.IsForwarding(insertedIndex));

            var rid = page.ReadForwarding(insertedIndex);
            Assert.Equal(5, rid.PageIndex);
            Assert.Equal(2, rid.SlotIndex);

            // Leggere un record marcato come forwarding non è supportato direttamente da Read
            ApplicationException? thrown = null;
            try
            {
                page.Read(insertedIndex);
            }
            catch (ApplicationException ex)
            {
                thrown = ex;
            }
            Assert.NotNull(thrown);
        }

        [Fact]
        public void Page_ReadForwarding_WhenRecordIsNotForwarding_ShouldThrow()
        {
            var page = new Page(new byte[4096]);
            page.Init();

            var insertedIndex = page.Insert(new byte[] { 1, 2, 3 });

            InvalidOperationException? thrown = null;
            try
            {
                page.ReadForwarding(insertedIndex);
            }
            catch (InvalidOperationException ex)
            {
                thrown = ex;
            }
            Assert.NotNull(thrown);
        }

        [Fact]
        public void Page_MarkAsForwarded_WhenRecordIsTooSmallToHoldPointer_ButPageHasSpace_ShouldSucceedViaCompaction()
        {
            var page = new Page(new byte[4096]);
            page.Init();

            // Il record ha meno byte di FORWARDING_RID_SIZE: non c'è spazio per scriverci il puntatore
            // in-place, ma la pagina è per il resto vuota, quindi la compattazione (che qui è un no-op,
            // dato che non c'è nulla da compattare) libera comunque spazio sufficiente.
            var insertedIndex = page.Insert(new byte[] { 1, 2, 3 });

            page.MarkAsForwarded(new RecordIdentifier(2, 0), insertedIndex);

            Assert.True(page.IsForwarding(insertedIndex));
            var rid = page.ReadForwarding(insertedIndex);
            Assert.Equal(2, rid.PageIndex);
            Assert.Equal(0, rid.SlotIndex);
        }

        [Fact]
        public void Page_TryUpdate_WhenRecordIsDeleted_ShouldThrow()
        {
            var page = new Page(new byte[4096]);
            page.Init();

            var insertedIndex = page.Insert(new byte[] { 1, 2, 3 });
            page.Delete(insertedIndex);

            InvalidOperationException? thrown = null;
            try
            {
                page.TryUpdate(insertedIndex, new byte[] { 9 });
            }
            catch (InvalidOperationException ex)
            {
                thrown = ex;
            }
            Assert.NotNull(thrown);
        }

        [Fact]
        public void Page_TryUpdate_WhenRecordIsForwarding_ShouldThrow()
        {
            var page = new Page(new byte[4096]);
            page.Init();

            var insertedIndex = page.Insert(new byte[] { 1, 2, 3, 4, 5, 6 });
            page.MarkAsForwarded(new RecordIdentifier(3, 1), insertedIndex);

            InvalidOperationException? thrown = null;
            try
            {
                page.TryUpdate(insertedIndex, new byte[] { 9 });
            }
            catch (InvalidOperationException ex)
            {
                thrown = ex;
            }
            Assert.NotNull(thrown);
        }


        [Fact]
        public void Page_Insert_WhenThereIsSpace_ShouldInsert()
        {
            var page = new Page(new byte[4096]);
            page.Init();

            var toInsert = new byte[] { 1, 2, 3 };
            var insertedIndex = page.Insert(toInsert);
            var read = page.Read(insertedIndex);

            Assert.Equal(toInsert, read.ToArray());
            Assert.Equal(0, insertedIndex);
        }

        [Fact]
        public void Page_Insert_WhenSizeIsTooLarge_ShouldThrow()
        {
            Assert.Throws<InvalidOperationException>(() =>
            {
                var page = new Page(new byte[4096]);
                page.Init();
                page.Insert(new byte[4090]);
            });
        }

        [Fact]
        public void Page_Insert_WhenSizeIsExactlyMaxAllowed_ShouldInsert()
        {
            var page = new Page(new byte[4096]);
            page.Init();

            var toInsert = new byte[Constants.SingleRecordMaxSize];
            var insertedIndex = page.Insert(toInsert);
            var read = page.Read(insertedIndex);

            Assert.Equal(0, insertedIndex);
            Assert.Equal(toInsert, read.ToArray());
        }

        [Fact]
        public void Page_Insert_WhenDataIsEmpty_ShouldThrow()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
            {
                var page = new Page(new byte[4096]);
                page.Init();
                page.Insert(Array.Empty<byte>());
            });
        }

        [Fact]
        public void Page_Insert_WhenPageIsNotInitialized_ShouldThrow()
        {
            Assert.Throws<InvalidOperationException>(() =>
            {
                var page = new Page(new byte[4096]);
                page.Insert(new byte[] { 1, 2, 3 });
            });
        }

        [Fact]
        public void Page_Insert_WhenThereIsNoSpace_ShouldThrow()
        {
            var page = new Page(new byte[4096]);
            page.Init();
            var iterazioni = 100;
            var toInsertSize = 10;

            for (int i = 0; i < iterazioni; i++)
            {
                page.Insert(new byte[toInsertSize]);
            }

            var byteSize = Constants.PAGE_SIZE - Constants.PAGE_HEADER_SIZE - (iterazioni * toInsertSize) - (iterazioni * Constants.PAGE_SLOT_SIZE) - Constants.PAGE_SLOT_SIZE; // 4 byte sono dello slot

            var insertedIndex = page.Insert(new byte[byteSize]);

            Assert.Equal(iterazioni, insertedIndex); // inserisce con indice 100

            var notInsertedIndex = page.Insert(new byte[1]); // non c'è più spazio
            Assert.Equal(-1, notInsertedIndex);
        }

        [Fact]
        public void Page_Delete_ShouldMarkRecordAsDeletedAndPreventRead()
        {
            var page = new Page(new byte[4096]);
            page.Init();

            var insertedIndex = page.Insert(new byte[] { 1, 2, 3 });
            page.Delete(insertedIndex);

            // 1. provo a rileggerlo e non me lo deve trovare
            InvalidOperationException? thrown = null;
            try
            {
                page.Read(insertedIndex);
            }
            catch (InvalidOperationException ex)
            {
                thrown = ex;
            }
            Assert.NotNull(thrown);

            // 2. IsDeleted mi torna true
            Assert.True(page.IsDeleted(insertedIndex));
        }

        [Fact]
        public void Page_Update_WhenLengthIsSmaller_ShouldUpdateCorrectly()
        {
            var page = new Page(new byte[4096]);
            page.Init();

            var insertedIndex = page.Insert(new byte[] { 1, 2, 3, 4, 5 });
            var updatedData = new byte[] { 9, 9 };

            var success = page.TryUpdate(insertedIndex, updatedData);

            Assert.True(success);
            Assert.Equal(updatedData, page.Read(insertedIndex).ToArray());
        }

        [Fact]
        public void Page_Update_WhenLengthIsEqual_ShouldUpdateCorrectly()
        {
            var page = new Page(new byte[4096]);
            page.Init();

            var insertedIndex = page.Insert(new byte[] { 1, 2, 3 });
            var updatedData = new byte[] { 9, 8, 7 };

            var success = page.TryUpdate(insertedIndex, updatedData);

            Assert.True(success);
            Assert.Equal(updatedData, page.Read(insertedIndex).ToArray());
        }

        [Fact]
        public void Page_Update_WhenLengthIsGreaterButThereIsSpace_ShouldMoveDataKeepingSameIndex()
        {
            var page = new Page(new byte[4096]);
            page.Init();

            var insertedIndex = page.Insert(new byte[] { 1, 2, 3 });
            var updatedData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

            var success = page.TryUpdate(insertedIndex, updatedData);

            Assert.True(success);

            // Il vecchio indice deve continuare a funzionare e non risultare eliminato
            Assert.False(page.IsDeleted(insertedIndex));

            // Leggendo tramite il vecchio indice trovo il dato aggiornato, spostato in nuova posizione
            Assert.Equal(updatedData, page.Read(insertedIndex).ToArray());
        }

        [Fact]
        public void Page_Update_WhenLengthIsGreaterAndThereIsNoSpace_ShouldFail()
        {
            var page = new Page(new byte[4096]);
            page.Init();
            var iterazioni = 100;
            var toInsertSize = 10;

            for (int i = 0; i < iterazioni; i++)
            {
                page.Insert(new byte[toInsertSize]);
            }

            // Riempio quasi completamente lo spazio residuo, lasciandone pochissimo libero
            var byteSize = Constants.PAGE_SIZE - Constants.PAGE_HEADER_SIZE - (iterazioni * toInsertSize) - (iterazioni * Constants.PAGE_SLOT_SIZE) - Constants.PAGE_SLOT_SIZE;
            var insertedIndex = page.Insert(new byte[byteSize]);

            // Provo ad aggiornare con una lunghezza maggiore di quella disponibile
            var success = page.TryUpdate(insertedIndex, new byte[byteSize + 1]);

            Assert.False(success);

            // Il record originale non deve essere stato toccato
            Assert.False(page.IsDeleted(insertedIndex));
            Assert.Equal(new byte[byteSize], page.Read(insertedIndex).ToArray());
        }
    }
}
