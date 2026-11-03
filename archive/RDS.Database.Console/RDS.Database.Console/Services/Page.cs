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
    // HEADER: 
    // - 1 byte: tipo di pagina (D = Data)
    // - 2 byte: numero di record / slot
    // - 2 byte: offset fine area dati
    // - 4 byte: indice prossima pagina (se non c'è: zero)
    //
    // SLOT DIRECTORY:
    // - 2 byte: offset record 1
    // - 2 byte: lunghezza record 1 (eliminato, vale zero)
    // - 2 byte: offset record 2
    // - 2 byte: lunghezza record 2 (migrato: vale 0xFFFF, cioè 65535)
    // - ...
    // - 2 byte: offset record n
    // - 2 byte: lunghezza record n
    //
    //
    // FREE SPACE:
    // 
    //   
    // RECORDS:
    // - record n
    // ...
    // - record 2 (migrato: contiene il RID del record migrato)
    //      - 4 byte: indice pagina record migrato
    //      - 2 byte: indice slot record migrato
    // - record 1 (spazio libero perchè il record è eliminato e lo slot è marcato con la tombstone)
    public readonly ref struct Page
    {
        private readonly ushort _recordCount => BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(1, 2));
        private readonly Span<byte> _data;
        public readonly ReadOnlySpan<byte> Data => _data;

        #region Private Methods
        private readonly ushort GetDataAreaEndOffset()
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
            var occupiedSpace = Constants.PAGE_HEADER_SIZE + (_recordCount * Constants.PAGE_SLOT_SIZE) + (Constants.PAGE_SIZE - GetDataAreaEndOffset());
            return Constants.PAGE_SIZE - occupiedSpace;
        }

        private readonly int GetTotalFreeSpace()
        {
            int dataOccupiedSpace = 0;
            int recordSize;
            for (ushort i = 0; i < _recordCount; i++)
            {
                recordSize = GetRecordSize(i);
                if (recordSize == Constants.FORWARDING_MARKER)
                {
                    recordSize = Constants.FORWARDING_RID_SIZE;
                }
                dataOccupiedSpace += recordSize;
            }

            return Constants.PAGE_SIZE - Constants.PAGE_HEADER_SIZE - (Constants.PAGE_SLOT_SIZE * _recordCount) - dataOccupiedSpace;
        }

        private readonly void Compact()
        {
            ushort currentSlotIndex;
            ushort currentRecordSize;
            ushort currentRecordOffset;
            ushort newOffset = Constants.PAGE_SIZE;
            var data = new byte[Constants.PAGE_SIZE];

            // Copio l'header e la directory degli slot nella nuova pagina
            _data[..(Constants.PAGE_HEADER_SIZE + (Constants.PAGE_SLOT_SIZE * _recordCount))].CopyTo(data.AsSpan(0, Constants.PAGE_HEADER_SIZE + (Constants.PAGE_SLOT_SIZE * _recordCount)));

            for (ushort i = 0; i < _recordCount; i++)
            {
                currentSlotIndex = i;
                currentRecordSize = GetRecordSize(currentSlotIndex);
                currentRecordOffset = GetRecordOffset(currentSlotIndex);

                // Non devo copiare nulla per i record eliminati
                if (currentRecordSize == 0)
                {
                    continue;
                }

                // L'informazione del forwarded sta nell'area dati e va copiata
                if (currentRecordSize == Constants.FORWARDING_MARKER)
                {
                    currentRecordSize = Constants.FORWARDING_RID_SIZE;
                }

                // Scrivo il record in fondo e aggiorno l'offset nello slot
                newOffset = (ushort)(newOffset - currentRecordSize);
                _data.Slice(currentRecordOffset, currentRecordSize).CopyTo(data.AsSpan(newOffset, currentRecordSize));
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(Constants.PAGE_HEADER_SIZE + (currentSlotIndex * Constants.PAGE_SLOT_SIZE), 2), newOffset);
            }

            data.CopyTo(_data);
            SetRecordsOffset(newOffset);
        }

        private readonly bool TryCompact(int requiredSpace)
        {
            if (GetTotalFreeSpace() >= requiredSpace)
            {
                Compact();
                return true;
            }

            return false;
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
            if (_data[0] != (byte)Constants.PAGE_TYPE_DATA && _data[0] != (byte)Constants.PAGE_TYPE_INDEX_INTERNAL)
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

            if (GetContiguousFreeSpace() < data.Length + Constants.PAGE_SLOT_SIZE && !TryCompact(data.Length + Constants.PAGE_SLOT_SIZE))
            {
                return -1;
            }

            var newRecordOffset = (ushort)(GetDataAreaEndOffset() - data.Length);
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
                throw new ApplicationException("Il record è stato migrato");
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

            // Se è più grande ma c'è abbastanza spazio nella pagina, lo sposto (faccio il check anche dopo aver compattato)
            if (GetContiguousFreeSpace() >= data.Length || TryCompact(data.Length))
            {
                // Scrivo nello spazio libero, partendo dalla fine verso l'inizio (come in Insert)
                var newRecordOffset = (ushort)(GetDataAreaEndOffset() - data.Length);
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
            bool canWrite = false;
            ushort offset = 0;

            if (existingRecordSize >= Constants.FORWARDING_RID_SIZE)
            {
                offset = GetRecordOffset(index);
                canWrite = true;
            }
            else if (TryCompact(Constants.FORWARDING_RID_SIZE))
            {
                offset = (ushort)(GetDataAreaEndOffset() - Constants.FORWARDING_RID_SIZE);
                SetRecordOffset(index, offset);

                // Sto scrivendo in fondo alla pagina
                SetRecordsOffset(offset);
                canWrite = true;
            }

            if (!canWrite || offset == 0)
            {
                throw new InvalidOperationException("Non c'è spazio per il rid della migrazione!");
            }

            // Marco il record come migrato con la costante al posto della size
            SetRecordSize(index, Constants.FORWARDING_MARKER);

            // Scrivo all'offset del record, nell'area dati, il rid ricevuto in input
            BinaryPrimitives.WriteUInt32LittleEndian(_data.Slice(offset, 4), (uint)identifier.PageIndex);
            BinaryPrimitives.WriteUInt16LittleEndian(_data.Slice(offset + 4, 2), (ushort)identifier.SlotIndex);
        }
    }
}

