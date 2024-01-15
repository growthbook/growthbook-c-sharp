using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using GrowthBook.Exceptions;

namespace GrowthBook.Utilities
{
    internal static class CryptographicUtilities
    {
        public static string Decrypt(string encryptedString, string decryptionKey)
        {
            try
            {
                var parts = encryptedString.Split('.');

                var iv = Convert.FromBase64String(parts[0]);
                var cipherBytes = Convert.FromBase64String(parts[1]);
                var keyBytes = Convert.FromBase64String(decryptionKey);

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
