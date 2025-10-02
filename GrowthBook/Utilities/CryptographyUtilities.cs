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
        public static string? Decrypt(string? encryptedString, string? decryptionKey)
        {
            try
            {
                var parts = encryptedString?.Split('.');

                var iv = Convert.FromBase64String(parts?[0] ?? string.Empty);
                var cipherBytes = Convert.FromBase64String(parts?[1] ?? string.Empty);
                var keyBytes = Convert.FromBase64String(decryptionKey ?? "");

                // Right now we're using the AES 128 CBC algorithm.


                //

                using (var aesProvider = Aes.Create())
                {
                    if (aesProvider == null)
                        throw new InvalidOperationException("Unable to create AES provider.");

                    aesProvider.BlockSize = 128;
                    aesProvider.KeySize = 256;
                    aesProvider.Key = keyBytes;
                    aesProvider.IV = iv;
                    aesProvider.Padding = PaddingMode.None;
                    aesProvider.Mode = CipherMode.CBC;

                    using (var decryptor = aesProvider.CreateDecryptor())
                    {
                        var decryptedBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);

                        // Обрізка повторюваних символів наприкінці
                        byte last = 0;

                        for (var i = decryptedBytes.Length - 1; i >= 0; i--)
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
            }
            catch (FormatException ex)
            {
                throw new DecryptionException("A value was not in the correct format", ex);
            }
            catch (Exception ex)
            {
                throw new DecryptionException("Unhandled exception occurred during decryption", ex);
            }
        }
    }
}
