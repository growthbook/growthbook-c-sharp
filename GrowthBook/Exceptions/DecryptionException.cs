using System;
using System.Collections.Generic;
using System.Text;

namespace GrowthBook.Exceptions
{
    /// <summary>
    /// Represents an error that occurred during the decryption of encrypted data.
    /// </summary>
    public sealed class DecryptionException : Exception
    {
        public DecryptionException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
