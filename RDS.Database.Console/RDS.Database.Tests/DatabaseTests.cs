using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using RDS.Database.Console.Services;
using RDS.Database.Console.Services.Models;
using Xunit;

namespace RDS.Database.Tests
{
    // Record di test con 3 proprietà: Id (int), Name (string), Value (double)
    public class Record
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public double Value { get; set; }

        // Serializza il record in un array di byte:
        // - 4 byte per Id
        // - 4 byte per la lunghezza (in byte) di Name
        // - N byte per Name (UTF-8)
        // - 8 byte per Value
        public byte[] ToBytes()
        {
            var nameBytes = Encoding.UTF8.GetBytes(Name);
            var buffer = new byte[4 + 4 + nameBytes.Length + 8];

            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), Id);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4, 4), nameBytes.Length);
            nameBytes.CopyTo(buffer.AsSpan(8, nameBytes.Length));
            BinaryPrimitives.WriteDoubleLittleEndian(buffer.AsSpan(8 + nameBytes.Length, 8), Value);

            return buffer;
        }

        public static Record FromBytes(ReadOnlySpan<byte> bytes)
        {
            var id = BinaryPrimitives.ReadInt32LittleEndian(bytes[..4]);
            var nameLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(4, 4));
            var name = Encoding.UTF8.GetString(bytes.Slice(8, nameLength));
            var value = BinaryPrimitives.ReadDoubleLittleEndian(bytes.Slice(8 + nameLength, 8));

            return new Record { Id = id, Name = name, Value = value };
        }
    }

    public class DatabaseTests : IDisposable
    {
        private readonly string _path;

        public DatabaseTests()
        {
            _path = Path.Combine(Path.GetTempPath(), $"database_tests_{Guid.NewGuid()}.db");
        }

        public void Dispose()
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }

        [Fact]
        public void CreateWriteSyncReopenRead_ShouldPreserveData()
        {
            var records = new List<Record>
            {
                new Record { Id = 1, Name = "Alpha", Value = 3.14 },
                new Record { Id = 2, Name = "Beta", Value = -42.5 },
                new Record { Id = 3, Name = "Gamma", Value = 0.0 },
                new Record { Id = 4, Name = "Delta con spazi e più caratteri", Value = 12345.6789 },
                new Record { Id = 5, Name = "", Value = double.MaxValue }
            };

            var positions = new List<RecordIdentifier>();

            using (var database = new Console.Services.Database(_path))
            {
                foreach (var record in records)
                {
                    var position = database.Table.Insert(record.ToBytes());
                    positions.Add(position);
                }

                database.Sync();
            }

            // Riapro il database su un nuovo oggetto Database (nuovo pager) e verifico i dati.
            using (var database = new Console.Services.Database(_path))
            {
                for (int i = 0; i < records.Count; i++)
                {
                    var identifier = positions[i];
                    var readBytes = database.Table.Read(identifier);
                    var readRecord = Record.FromBytes(readBytes);

                    Assert.Equal(records[i].Id, readRecord.Id);
                    Assert.Equal(records[i].Name, readRecord.Name);
                    Assert.Equal(records[i].Value, readRecord.Value);
                }

                // Verifico anche tramite scan completo che l'ordine e il contenuto siano corretti.
                var scanner = database.Table.Scan();
                var scannedCount = 0;

                while (scanner.MoveNext())
                {
                    var expected = records[scannedCount];
                    var readRecord = Record.FromBytes(scanner.Current);

                    Assert.Equal(expected.Id, readRecord.Id);
                    Assert.Equal(expected.Name, readRecord.Name);
                    Assert.Equal(expected.Value, readRecord.Value);

                    scannedCount++;
                }

                Assert.Equal(records.Count, scannedCount);
            }
        }
    }
}
