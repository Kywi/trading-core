using System;
using System.Security.Cryptography;
using System.Text;

namespace GripTrader.Core.Utils
{
    public class SignatureHelper
    {
        public static string Sign(string source, string key)
        {
            var keyBytes = Encoding.UTF8.GetBytes(key);
            var sourceBytes = Encoding.UTF8.GetBytes(source);
            var hash = HMACSHA256.HashData(keyBytes, sourceBytes);
            return Convert.ToHexStringLower(hash);
        }
    }
}