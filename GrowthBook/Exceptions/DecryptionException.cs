using System;
using System.Collections.Generic;
using System.Text;

namespace GrowthBook.Exceptions
{
    public sealed class DecryptionException : Exception
    {
        public DecryptionException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
