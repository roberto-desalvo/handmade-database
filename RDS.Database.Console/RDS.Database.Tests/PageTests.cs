using RDS.Database.Console.Services;
using RDS.Database.Console.Services.Utilities;

namespace RDS.Database.Tests
{
    public class PageTests
    {
        
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
