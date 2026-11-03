using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RDS.Database.Console.Services.Utilities
{
    public static class Guard
    {
        public static void ThrowIfOverMaxRecordLength(int size)
        {
            if (size > Constants.SingleRecordMaxSize)
            {
                throw new InvalidOperationException($"Il record ha dimensione {size} che eccede la dimensione massima consentita {Constants.SingleRecordMaxSize}.");
            }
        }

        public static void ThrowIfLessThanOrEqualToZero(int value, string paramName)
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(paramName, "Il valore deve essere maggiore di zero.");
            }
        }

        public static void ThrowIfLessThanZero(int value, string paramName)
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(paramName, "Il valore deve essere maggiore o uguale a zero.");
            }
        }

        public static void ThrowIfPageBufferHasWrongSize(Span<byte> buffer)
        {
            if (buffer.Length != Constants.PAGE_SIZE)
            {
                throw new ArgumentException($"Il buffer deve essere di {Constants.PAGE_SIZE} byte");
            }
        }

        public static void ThrowIfPageBufferHasWrongSize(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length != Constants.PAGE_SIZE)
            {
                throw new ArgumentException($"Il buffer deve essere di {Constants.PAGE_SIZE} byte");
            }
        }
    }
}
