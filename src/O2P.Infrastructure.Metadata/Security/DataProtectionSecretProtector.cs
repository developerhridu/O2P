using Microsoft.AspNetCore.DataProtection;
using O2P.Application.Interfaces;
using System;
using System.Text;

namespace O2P.Infrastructure.Metadata.Security
{
    public class DataProtectionSecretProtector : ISecretProtector
    {
        private const string Prefix = "o2p:v1:";
        private readonly IDataProtector _protector;

        public DataProtectionSecretProtector(IDataProtectionProvider provider)
        {
            _protector = provider.CreateProtector("O2P.ConnectionSecrets.v1");
        }

        public byte[] Protect(string plaintext)
        {
            var protectedValue = _protector.Protect(plaintext ?? string.Empty);
            return Encoding.UTF8.GetBytes(Prefix + protectedValue);
        }

        public string Unprotect(byte[] protectedBytes)
        {
            if (protectedBytes == null || protectedBytes.Length == 0)
            {
                return string.Empty;
            }

            var encoded = Encoding.UTF8.GetString(protectedBytes);
            if (!encoded.StartsWith(Prefix, StringComparison.Ordinal))
            {
                // Legacy rows were stored as raw UTF-8 bytes. Keep reading them so they can be backfilled.
                return encoded;
            }

            return _protector.Unprotect(encoded.Substring(Prefix.Length));
        }

        public bool IsProtected(byte[]? value)
        {
            if (value == null || value.Length == 0)
            {
                return false;
            }

            return Encoding.UTF8.GetString(value).StartsWith(Prefix, StringComparison.Ordinal);
        }
    }
}
