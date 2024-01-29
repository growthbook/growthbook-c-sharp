using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using GrowthBook.Exceptions;

namespace GrowthBook.Utilities
{
    internal static class CryptographyUtilities
    {
        /// <summary>
        /// Attempts to decrypt the encrypted value using the provided decryption key.
        /// </summary>
        /// <param name="encryptedString">The encrypted value.</param>
        /// <param name="decryptionKey">The caller's decryption key.</param>
        /// <returns>The decrypted data. Note that if the key is incorrect, this will return garbage data that will not be usable.</returns>
        /// <exception cref="DecryptionException">Thrown if an exception is encountered during decryption.</exception>
        public static string Decrypt(string encryptedString, string decryptionKey)
        {
            try
            {
                var parts = encryptedString.Split('.');

                var iv = Convert.FromBase64String(parts[0]);
                var cipherBytes = Convert.FromBase64String(parts[1]);
                var keyBytes = Convert.FromBase64String(decryptionKey);

                // Right now we're using the AES 128 CBC algorithm.

                var aesProvider = new AesCryptoServiceProvider
                {
                    BlockSize = 128,
                    KeySize = 256,
                    Key = keyBytes,
                    IV = iv,
                    Padding = PaddingMode.None,
                    Mode = CipherMode.CBC
                };

                using (var decryptor = aesProvider.CreateDecryptor(keyBytes, iv))
                {
                    var decryptedBytes = decryptor.TransformFinalBlock(
                        cipherBytes, 0, cipherBytes.Length);

                    // The .Net decryptor will pad the end of the decrypted plaintext results
                    // with a repeating garbage character, presumably to meet buffer length.
                    // We're assuming at this point that any repeating character at the end
                    // should be stripped off before sending back the remains as the decrypted result.

                    byte last = 0;

                    for(var i = decryptedBytes.Length - 1; i >= 0; i--)
                    {
                        if (i == decryptedBytes.Length - 1)
                        {
                            last = decryptedBytes[i];
                            continue;
                        }

                        if (decryptedBytes[i] == last)
                        {
                            continue;
                        }

                        var result = new byte[i + 1];

                        Array.Copy(decryptedBytes, result, i + 1);

                        return Encoding.UTF8.GetString(result);
                    }

                    return null;
                }
            }
            catch(FormatException ex)
            {
                throw new DecryptionException("A value was not in the correct format", ex);
            }
            catch(Exception ex)
            {
                throw new DecryptionException("Unhandled exception occurred during decryption", ex);
            }
        }
    }
}
