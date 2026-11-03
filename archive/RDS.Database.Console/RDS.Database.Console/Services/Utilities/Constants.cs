using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RDS.Database.Console.Services.Utilities
{
    public static class Constants
    {
        public const string DATABASE_FILE_NAME = "database.db";
        public const string DATABASE_FILE_EXTENSION = ".db";
        public const string DATABASE_FILE_PATH = "C:\\Users\\roberto.de.salvo\\source\\repos\\RDS.Database.Console\\RDS.Database.Console\\data\\" + DATABASE_FILE_NAME;

        #region Slotted Page Constants
        public const int PAGE_SIZE = 4096;

        // La pagina ha un header di 5 byte:
        // - 1 byte per il tipo di pagina (D = Data, I = Index)
        // - 2 byte per il numero di record / slot
        // - 2 byte per l'offset del primo record da inserire (dove finisce l'ultimo record inserito)
        // - 4 byte per la prossima pagina (se non c'è ci sarà zero)
        public const int PAGE_HEADER_SIZE = 9;

        // Nello slot ci sono:
        // - 2 byte per l'offset del record
        // - 2 byte per la lunghezza del record
        public const int PAGE_SLOT_SIZE = 4;

        public const char PAGE_TYPE_DATA = 'D';
        public const char PAGE_TYPE_INDEX_INTERNAL = 'I';
        public const char PAGE_TYPE_INDEX_LEAF = 'L';

        public const int INDEX_INTERNAL_HEADER_SIZE = 4;
        public const int INDEX_KEY_SIZE = 4;
        public const int INDEX_POINTER_SIZE = 4;
        public const int INDEX_INTERNAL_FANAOUT = 512; // Numero massimo di figli
        #endregion

        public static int SingleRecordMaxSize => PAGE_SIZE - PAGE_HEADER_SIZE - PAGE_SLOT_SIZE;

        public static readonly byte[] MAGIC_NUMBER = [0x44, 0x42, 0x46, 0x31]; // DBF1

        public const ushort FORWARDING_MARKER = 0xFFFF; // 65535, non raggiungibile da una size reale
        public const int FORWARDING_RID_SIZE = 6;

    }
}
