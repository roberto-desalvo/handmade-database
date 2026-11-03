using Microsoft.Win32.SafeHandles;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using RDS.Database.Console.Services.Utilities;

namespace RDS.Database.Console.Services
{
    public class Pager : IDisposable
    {
        private readonly SafeFileHandle _handle;
        public int PagesCount => (int)(RandomAccess.GetLength(_handle) / Constants.PAGE_SIZE);

        public Pager(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentNullException(nameof(path));
            }

            // Apre il file del database in modalità lettura/scrittura e crea il file se non esiste
            _handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            if (_handle == null || _handle.IsInvalid)
            {
                throw new IOException("Impossibile aprire il file del database.");
            }

            if (RandomAccess.GetLength(_handle) % Constants.PAGE_SIZE != 0)
            {
                throw new IOException("Il file del database non rispetta la dimensione attesa.");
            }
        }

        #region Private Methods
        private void ThrowIfOffsetIsOutOfRange(int pageIndex)
        {
            if (pageIndex < 0 || pageIndex >= PagesCount)
            {
                throw new ArgumentException("La pagina richiesta non esiste nel file del database.");
            }
        }        
        #endregion

        public void Read(int pageIndex, Span<byte> buffer)
        {
            Guard.ThrowIfPageBufferHasWrongSize(buffer);
            ThrowIfOffsetIsOutOfRange(pageIndex);

            long offset = (long)pageIndex * Constants.PAGE_SIZE;
            var bytesRead = RandomAccess.Read(_handle, buffer, offset);
            if (bytesRead != Constants.PAGE_SIZE)
            {
                throw new IOException("Impossibile leggere l'intera pagina dal file del database.");
            }
        }

        public void Write(int pageIndex, ReadOnlySpan<byte> buffer)
        {
            Guard.ThrowIfPageBufferHasWrongSize(buffer);
            ThrowIfOffsetIsOutOfRange(pageIndex);

            long offset = (long)pageIndex * Constants.PAGE_SIZE;
            RandomAccess.Write(_handle, buffer, offset);
        }

        public int AllocateNewPage()
        {
            Span<byte> buffer = stackalloc byte[Constants.PAGE_SIZE];
            var pageIndex = PagesCount; 
            long offset = (long)pageIndex * Constants.PAGE_SIZE;
            RandomAccess.Write(_handle, buffer, offset);
            return pageIndex;
        }

        public void Sync()
        {
            RandomAccess.FlushToDisk(_handle);
        }        

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            // Rilascia l'handle al SO così che altri possano accedere al file
            _handle.Dispose();
        }
    }
}
