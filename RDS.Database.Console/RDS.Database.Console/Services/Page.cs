using RDS.Database.Console.Services.Models;
using RDS.Database.Console.Services.Utilities;
using System.Buffers.Binary;

namespace RDS.Database.Console.Services
{
    // SLOTTED PAGE:
    //
    // HEADER    SLOT DIRECTORY               (FREE SPACE)                     RECORDS
    // * * * * | * * * * * * * * - - - - - - - - - - - - - - - - - - - * * * * * * * *
    //

    public ref struct Page
    {
        private readonly ushort _recordCount => BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(1, 2));
        private Span<byte> _data;
        public readonly ReadOnlySpan<byte> Data => _data;

        #region Private Methods
        private readonly ushort GetNextRecordOffset()
         => BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(3, 2));

        private readonly void SetRecordCount(ushort count)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_data.Slice(1, 2), count);
        }

        private readonly void SetRecordsOffset(ushort offset)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_data.Slice(3, 2), offset);
        }

        private readonly ushort GetRecordOffset(int index)
        {
            return BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(Constants.PAGE_HEADER_SIZE + (index * Constants.PAGE_SLOT_SIZE), 2));
        }

        private readonly ushort GetRecordSize(int index)
        {
            return BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(Constants.PAGE_HEADER_SIZE + (index * Constants.PAGE_SLOT_SIZE) + 2, 2));
        }

        private readonly void SetRecordOffset(int index, ushort offset)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_data.Slice(Constants.PAGE_HEADER_SIZE + (index * Constants.PAGE_SLOT_SIZE), 2), offset);
        }

        private readonly void SetRecordSize(int index, ushort size)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_data.Slice(Constants.PAGE_HEADER_SIZE + (index * Constants.PAGE_SLOT_SIZE) + 2, 2), size);
        }

        private readonly void CheckValidIndex(int index)
        {
            Guard.ThrowIfLessThanZero(index, nameof(index));
            if (index >= _recordCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "L'indice indicato non è valido");
            }
        }

        private readonly int GetContiguousFreeSpace()
        {
            // totale - header - slot - (totale - offset del prossimo record = la parte finale occupata dai record)
            var occupiedSpace = Constants.PAGE_HEADER_SIZE + (_recordCount * Constants.PAGE_SLOT_SIZE) + (Constants.PAGE_SIZE - GetNextRecordOffset());
            return Constants.PAGE_SIZE - occupiedSpace;
        }
        #endregion

        public Page(Span<byte> page)
        {
            Guard.ThrowIfPageBufferHasWrongSize(page);
            _data = page;
        }

        public readonly void Init(char pageType = Constants.PAGE_TYPE_DATA)
        {
            _data.Clear();

            _data[0] = (byte)pageType;
            SetRecordCount(0);
            SetRecordsOffset(Constants.PAGE_SIZE);
        }

        public readonly void CheckInitialized()
        {
            if (_data[0] != (byte)Constants.PAGE_TYPE_DATA && _data[0] != (byte)Constants.PAGE_TYPE_INDEX)
            {
                throw new InvalidOperationException("La pagina non è inizializzata");
            }
        }

        public readonly ushort GetRecordCount()
        {
            CheckInitialized();
            return _recordCount;
        }

        public readonly int GetNextPageIndex()
        {
            CheckInitialized();
            return BinaryPrimitives.ReadInt32LittleEndian(_data.Slice(5, 4)); // Leggo l'header da byte 5 a byte 8 (4 byte) per ottenere l'indice della prossima pagina
        }

        public readonly void SetNextPageIndex(int nextPageIndex)
        {
            CheckInitialized();
            Guard.ThrowIfLessThanOrEqualToZero(nextPageIndex, nameof(nextPageIndex)); // Zero è riservato per l'header del database

            BinaryPrimitives.WriteInt32LittleEndian(_data.Slice(5, 4), nextPageIndex); // Scrivo l'header per impostare l'indice della prossima pagina
        }

        public readonly int Insert(ReadOnlySpan<byte> data)
        {
            CheckInitialized();
            Guard.ThrowIfLessThanOrEqualToZero(data.Length, nameof(data.Length));
            Guard.ThrowIfOverMaxRecordLength(data.Length);

            var index = _recordCount;

            if (GetContiguousFreeSpace() < data.Length + Constants.PAGE_SLOT_SIZE)
            {
                return -1;
            }

            var newRecordOffset = (ushort)(GetNextRecordOffset() - data.Length);
            var dataSize = (ushort)data.Length;

            // Scrivo lo slot 
            SetRecordOffset(index, newRecordOffset);
            SetRecordSize(index, dataSize);

            // Scrivo il dato nella pagina
            data.CopyTo(_data.Slice(newRecordOffset, dataSize));

            // Aggiorno l'header
            SetRecordCount((ushort)(index + 1));
            SetRecordsOffset(newRecordOffset);

            return index;
        }

        public readonly ReadOnlySpan<byte> Read(int index)
        {
            CheckInitialized();
            CheckValidIndex(index);

            var recordSize = GetRecordSize(index);

            if (recordSize == 0)
            {
                throw new InvalidOperationException("Il record è stato eliminato");
            }

            if (recordSize == Constants.FORWARDING_MARKER)
            {
                // TODO: decidere come segnalare il forwarding al chiamante
                throw new ApplicationException("Forwarding");
            }

            return _data.Slice(GetRecordOffset(index), recordSize);
        }

        public readonly RecordIdentifier ReadForwarding(int index)
        {
            CheckInitialized();
            CheckValidIndex(index);

            if (GetRecordSize(index) != Constants.FORWARDING_MARKER)
            {
                throw new InvalidOperationException("Non è un forwarding");
            }

            var pageIndex = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(GetRecordOffset(index), 4));
            var slotIndex = BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(GetRecordOffset(index) + 4, 2));
            return new RecordIdentifier((int)pageIndex, slotIndex);
        }

        public readonly void Delete(int index)
        {
            CheckInitialized();
            CheckValidIndex(index);

            SetRecordSize(index, 0); // Imposto la dimensione del record a 0 per indicare che è stato eliminato
        }

        public readonly bool IsDeleted(int index)
        {
            CheckInitialized();
            CheckValidIndex(index);
            return GetRecordSize(index) == 0;
        }

        public readonly bool IsForwarding(int index)
        {
            CheckInitialized();
            CheckValidIndex(index);
            return GetRecordSize(index) == Constants.FORWARDING_MARKER;
        }

        public readonly bool TryUpdate(int index, ReadOnlySpan<byte> data)
        {
            CheckInitialized();
            CheckValidIndex(index);
            Guard.ThrowIfLessThanOrEqualToZero(data.Length, nameof(data.Length));
            Guard.ThrowIfOverMaxRecordLength(data.Length);

            if (IsDeleted(index))
            {
                throw new InvalidOperationException("Il record da aggiornare risulta eliminato.");
            }

            if (IsForwarding(index))
            {
                throw new InvalidOperationException("Il record da aggiornare risulta migrato");
            }

            var existingRecordSize = GetRecordSize(index);

            // Se è uguale o più piccolo, sovrascrivo e aggiorno la lunghezza nello slot
            if (data.Length <= existingRecordSize)
            {
                var existingRecordOffset = GetRecordOffset(index);
                data.CopyTo(_data.Slice(existingRecordOffset, data.Length));
                SetRecordSize(index, (ushort)data.Length);
                return true;
            }

            // Se è più grande ma c'è abbastanza spazio nella pagina, lo sposto 
            if (GetContiguousFreeSpace() >= data.Length)
            {
                // Scrivo nello spazio libero, partendo dalla fine verso l'inizio (come in Insert)
                var newRecordOffset = (ushort)(GetNextRecordOffset() - data.Length);
                data.CopyTo(_data.Slice(newRecordOffset, data.Length));

                // Aggiorno lo slot esistente: il vecchio indice continua a puntare al dato, ora spostato
                SetRecordOffset(index, newRecordOffset);
                SetRecordSize(index, (ushort)data.Length);
                SetRecordsOffset(newRecordOffset);
                return true;
            }

            return false;
        }

        public readonly void MarkAsForwarded(RecordIdentifier identifier, int index)
        {
            CheckInitialized();
            CheckValidIndex(index);

            var existingRecordSize = (ushort)GetRecordSize(index);

            if (existingRecordSize >= Constants.FORWARDING_RID_SIZE)
            {
                SetRecordSize(index, Constants.FORWARDING_MARKER);
                var offset = GetRecordOffset(index);
                BinaryPrimitives.WriteUInt32LittleEndian(_data.Slice(offset, 4), (uint)identifier.PageIndex);
                BinaryPrimitives.WriteUInt16LittleEndian(_data.Slice(offset + 4, 2), (ushort)identifier.SlotIndex);
            }
            else
            {
                // TODO: compatta + eventualmente segnala al chiamante nel caso peggiore
                throw new NotImplementedException();
            }
        }
    }
}

