using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RainbowMage.OverlayPlugin.Tts
{
    // Fork: one provider's settings as it reads them: secrets decrypted, missing ones defaulted.
    public class TtsSettings
    {
        private readonly Dictionary<string, string> values;

        public TtsSettings(ITtsProvider provider, TtsConfig config)
        {
            values = new Dictionary<string, string>();
            Dictionary<string, string> saved = null;
            config.ProviderSettings?.TryGetValue(provider.Id, out saved);
            foreach (var field in provider.Fields)
            {
                string value = null;
                saved?.TryGetValue(field.Key, out value);
                if (field.Kind == TtsFieldKind.Secret)
                    value = TtsSecrets.Unprotect(value);
                values[field.Key] = string.IsNullOrEmpty(value) ? field.Default : value;
            }
        }

        public string this[string key] => values.TryGetValue(key, out var v) ? v ?? "" : "";

        // What identifies the speech it makes (for the caches): everything but secrets.
        public string Fingerprint(ITtsProvider provider) =>
            string.Join("|", provider.Fields.Where(f => f.Kind != TtsFieldKind.Secret && !f.NotInKey).Select(f => this[f.Key]));
    }

    // Fork: API keys kept in the plugin config encrypted for this Windows user (DPAPI): the config
    // file alone (shared for support, copied to another PC) does not give the key away.
    public static class TtsSecrets
    {
        private const string Prefix = "dpapi:";
        private static readonly byte[] entropy = Encoding.UTF8.GetBytes("OverlayPlugin TTS");

        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain))
                return "";
            var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), entropy, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(data);
        }

        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored))
                return "";
            if (!stored.StartsWith(Prefix))
                return stored;
            try
            {
                var data = ProtectedData.Unprotect(Convert.FromBase64String(stored.Substring(Prefix.Length)), entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch (Exception)
            {
                // Another user's or PC's: as if not set.
                return "";
            }
        }
    }
}
