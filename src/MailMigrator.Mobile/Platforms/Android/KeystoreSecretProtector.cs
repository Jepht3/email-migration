using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using MailMigrator.Core.Abstractions;
using System.Text;

namespace MailMigrator.Mobile.Droid;

/// <summary>
/// Mots de passe chiffrés en AES-256-GCM avec une clé du Keystore Android : la clé ne quitte
/// jamais le matériel sécurisé du téléphone et n'est pas exportable.
/// </summary>
public sealed class KeystoreSecretProtector : ISecretProtector
{
    private const string Alias = "lewebmax_mail_migrator_v1";
    private const string Transformation = "AES/GCM/NoPadding";
    private const int IvLength = 12;

    private static IKey GetOrCreateKey()
    {
        var store = KeyStore.GetInstance("AndroidKeyStore")!;
        store.Load(null);
        if (store.GetKey(Alias, null) is IKey existing) return existing;

        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore")!;
        generator.Init(new KeyGenParameterSpec.Builder(Alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .SetKeySize(256)
            .Build());
        return generator.GenerateKey()!;
    }

    public byte[] Protect(string secret)
    {
        var cipher = Cipher.GetInstance(Transformation)!;
        cipher.Init(CipherMode.EncryptMode, GetOrCreateKey());
        var iv = cipher.GetIV()!;
        var encrypted = cipher.DoFinal(Encoding.UTF8.GetBytes(secret))!;
        return [.. iv, .. encrypted];
    }

    public string Unprotect(byte[] protectedSecret)
    {
        var cipher = Cipher.GetInstance(Transformation)!;
        cipher.Init(CipherMode.DecryptMode, GetOrCreateKey(), new GCMParameterSpec(128, protectedSecret[..IvLength]));
        return Encoding.UTF8.GetString(cipher.DoFinal(protectedSecret[IvLength..])!);
    }
}
