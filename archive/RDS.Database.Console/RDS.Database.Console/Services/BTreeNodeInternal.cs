using RDS.Database.Console.Services.Models;
using RDS.Database.Console.Services.Utilities;
using System.Buffers.Binary;

namespace RDS.Database.Console.Services
{
    // NODO INTERNO B+TREE:
    //
    // HEADER: 
    // - 1 byte: tipo di pagina (I = Internal)
    // - 2 byte: numero di chiavi (i puntatori saranno n + 1)
    // - 1 byte: vuoto, comunque non lo userei 
    //
    // KEYS DIRECTORY:
    // - 4 byte: puntatore alla pagina figlia (int): < chiave1
    // - 4 byte: chiave 1 (int)
    // - 4 byte: puntatore alla pagina figlia (int): >= chiave1 e < chiave2
    // - 4 byte: chiave 2 (int)
    // - 4 byte: puntatore alla pagina figlia (int): >= chiave2 

    // => Fanout = 512
    public readonly ref struct BTreeNodeInternal
    {
        private readonly ushort _keysCount => BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(1, 2));
        private readonly ushort _pointersCount => (ushort)(_keysCount + 1);
        private readonly Span<byte> _data;
        public readonly ReadOnlySpan<byte> Data => _data;

        public BTreeNodeInternal(Span<byte> data)
        {
            Guard.ThrowIfPageBufferHasWrongSize(data);
            _data = data;
        }

        public void Init()
        {
            _data.Clear();
            _data[0] = (byte)Constants.PAGE_TYPE_INDEX_INTERNAL;
            SetKeysCount(0);
        }

        public readonly void CheckInitialized()
        {
            if (_data[0] != (byte)Constants.PAGE_TYPE_INDEX_INTERNAL)
            {
                throw new InvalidOperationException("La pagina non è inizializzata");
            }
        }

        private void CheckValidPointerIndex(int index)
        {
            Guard.ThrowIfLessThanZero(index, nameof(index));
            if (index > _pointersCount - 1)
            {
                throw new InvalidOperationException("L'indice specificato non è valido!");
            }
        }

        private void CheckValidKeyIndex(int index)
        {
            Guard.ThrowIfLessThanZero(index, nameof(index));
            if (index > _keysCount - 1)
            {
                throw new InvalidOperationException("L'indice specificato non è valido!");
            }
        }

        private int GetPointerOffset(int index)
        {
            CheckValidPointerIndex(index);
            return Constants.INDEX_INTERNAL_HEADER_SIZE + (index * (Constants.INDEX_POINTER_SIZE + Constants.INDEX_KEY_SIZE));
        }

        private int GetKeyOffset(int index)
        {
            CheckValidKeyIndex(index);
            return Constants.INDEX_INTERNAL_HEADER_SIZE + Constants.INDEX_POINTER_SIZE + (index * (Constants.INDEX_POINTER_SIZE + Constants.INDEX_KEY_SIZE));
        }

        private void SetKeysCount(ushort count)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_data.Slice(1, 2), count);
        }

        private int ReadKey(int index)
        {
            var offset = GetKeyOffset(index);
            return BinaryPrimitives.ReadInt32LittleEndian(_data.Slice(offset, 4));
        }

        private int ReadPointer(int index)
        {
            var offset = GetPointerOffset(index);
            return BinaryPrimitives.ReadInt32LittleEndian(_data.Slice(offset, 4));
        }

        public int GetNodePointer(int key)
        {
            CheckInitialized();
            int currentKey;
            var keysCount = _keysCount;

            if (keysCount == 0)
            {
                throw new InvalidOperationException("Il nodo è vuoto!");
            }

            for (ushort i = 0; i < keysCount; i++)
            {
                currentKey = ReadKey(i);

                if (key < currentKey)
                {
                    return ReadPointer(i);
                }
            }

            return ReadPointer(keysCount);
        }
    }
}

