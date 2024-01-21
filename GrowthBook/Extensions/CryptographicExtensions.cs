using System;
using System.Collections.Generic;
using System.Text;
using GrowthBook.Utilities;

namespace GrowthBook.Extensions
{
    internal static class CryptographicExtensions
    {
        public static string DecryptWith(this string encryptedValue, string decryptionKey) => CryptographyUtilities.Decrypt(encryptedValue, decryptionKey);
    }
}
