using System.Security.Cryptography;
using System.Text;

namespace ToolPlus.Common
{
    /// <summary>
    /// Hashing and encoding helpers used by the session-token machinery. The algorithm and the
    /// output format (UPPERCASE hex) are part of what is already stored in ToolSession.TokenHash —
    /// change either and every existing session stops verifying.
    /// </summary>
    public static class Utility
    {
        public static string GetSHA256(string str) => GetSHA(str, SHA256.Create());

        public static string GetSHA512(string str) => GetSHA(str, SHA512.Create());

        private static string GetSHA(string str, HashAlgorithm hash)
        {
            try
            {
                string[] value = (from x in hash.ComputeHash(Encoding.UTF8.GetBytes(str))
                                  select x.ToString("X2")).ToArray();
                return string.Join("", value).ToUpper();
            }
            catch
            {
                return "";
            }
        }

        public static string Base64Encode(string plainText)
        {
            var plainTextBytes = Encoding.UTF8.GetBytes(plainText);
            return Convert.ToBase64String(plainTextBytes);
        }

        public static string Base64Decode(string base64EncodedData)
        {
            var base64EncodedBytes = Convert.FromBase64String(base64EncodedData);
            return Encoding.UTF8.GetString(base64EncodedBytes);
        }
    }
}
