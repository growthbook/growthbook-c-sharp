using System;
using System.Collections.Generic;
using System.Text;
using GrowthBook.Utilities;

namespace GrowthBook.Extensions
{
    internal static class CryptographicExtensions
    {
        /// <summary>
        /// Attempts to decrypt the encrypted value using the provided decryption key.
        /// </summary>
        /// <param name="encryptedValue">The encrypted value.</param>
        /// <param name="decryptionKey">The caller's decryption key.</param>
        /// <returns>The decrypted data. Note that if the key is incorrect, this will return garbage data that will not be usable.</returns>
        public static string DecryptWith(this string encryptedValue, string decryptionKey) => CryptographyUtilities.Decrypt(encryptedValue, decryptionKey);
    }
}
