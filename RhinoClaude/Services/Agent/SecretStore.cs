using System;
using System.Security.Cryptography;
using System.Text;

namespace RhinoClaude.Services.Agent
{
    /// <summary>
    /// Encrypts API keys before they reach Rhino's plugin settings file, and decrypts them on
    /// the way back.
    ///
    /// The settings XML sits in a plainly-named folder under %APPDATA% and was holding the key
    /// verbatim, so anything that could read the file — a backup, a screen share, a stray paste,
    /// an agent reading its own configuration — got a working credential. DPAPI ties the
    /// ciphertext to the current Windows user, so the file on its own is worth nothing on
    /// another account or machine.
    ///
    /// This is storage hardening, not secrecy from the process: the plugin necessarily holds the
    /// real key in memory to talk to the API.
    /// </summary>
    public static class SecretStore
    {
        /// <summary>Marks a value this class wrote. Anything without it is treated as plaintext.</summary>
        private const string Prefix = "dpapi:";

        /// <summary>
        /// Extra entropy, so a ciphertext lifted out of this file cannot be decrypted by some
        /// other DPAPI caller running as the same user without also knowing this constant.
        /// </summary>
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RhinoClaude.ApiKey.v1");

        /// <summary>
        /// Encrypt for storage. Returns the input unchanged if encryption is unavailable —
        /// losing the ability to save a key would be a worse failure than storing it as before.
        /// </summary>
        public static string Protect(string plaintext)
        {
            if (string.IsNullOrEmpty(plaintext)) return plaintext;
            if (IsProtected(plaintext)) return plaintext;

            try
            {
                byte[] cipher = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);
                return Prefix + Convert.ToBase64String(cipher);
            }
            catch (Exception)
            {
                return plaintext;
            }
        }

        /// <summary>
        /// Decrypt a stored value. Plaintext is passed straight through, so keys saved by an
        /// earlier build keep working and are re-encrypted the next time they are written.
        /// </summary>
        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return stored;
            if (!IsProtected(stored)) return stored;

            try
            {
                byte[] cipher = Convert.FromBase64String(stored.Substring(Prefix.Length));
                byte[] plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception)
            {
                // Wrong user, roamed profile, or a corrupted value. Returning null rather than
                // the ciphertext means the caller falls back to its environment variable and
                // reports "no key" instead of sending base64 noise to the API as a credential.
                return null;
            }
        }

        public static bool IsProtected(string value) =>
            !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);
    }
}
