using System.Security.Cryptography;
using AppleDeviceHub.Core.Updates;

// Release signing for Clasp's auto-update. The private key stays on the release owner's PC and is
// never committed; the app embeds only the public key (UpdateSignature.PublicKey).
//
//   new-key <private-key.pem>                      create a key pair; prints the public key to embed
//   sign    <private-key.pem> <installer> <version> write <installer>.sig
//   verify  <installer> <version>                  check <installer>.sig against the key in the app
return args switch
{
    ["new-key", var keyPath] => NewKey(keyPath),
    ["sign", var keyPath, var installer, var version] => Sign(keyPath, installer, version),
    ["verify", var installer, var version] => Verify(installer, version),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("usage: UpdateSigner new-key <private-key.pem> | sign <private-key.pem> <installer.exe> <X.Y.Z> | verify <installer.exe> <X.Y.Z>");
    return 2;
}

static int NewKey(string keyPath)
{
    if (File.Exists(keyPath))
    {
        Console.Error.WriteLine($"{keyPath} already exists. Refusing to overwrite a signing key.");
        return 1;
    }
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(keyPath))!);
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
    Console.WriteLine("Private key written to " + Path.GetFullPath(keyPath));
    Console.WriteLine("Public key (put this in UpdateSignature.PublicKey):");
    Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    return 0;
}

static int Sign(string keyPath, string installer, string versionText)
{
    if (!ClaspVersion.TryParse(versionText, out var version)) { Console.Error.WriteLine("Version must be X.Y.Z."); return 2; }
    var name = Path.GetFileName(installer);
    if (name != ReleaseFeed.InstallerName(version))
    {
        Console.Error.WriteLine($"Installer must be named {ReleaseFeed.InstallerName(version)}.");
        return 1;
    }
    using var key = ECDsa.Create();
    key.ImportFromPem(File.ReadAllText(keyPath));
    var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    if (publicKey != UpdateSignature.PublicKey)
    {
        Console.Error.WriteLine("This private key does not match the public key built into the app; the app would reject the signature.");
        return 1;
    }
    var hash = SHA256.HashData(File.ReadAllBytes(installer));
    var signature = UpdateSignature.Sign(key, version, name, hash);
    File.WriteAllText(installer + ".sig", signature + "\n");
    Console.WriteLine($"Signed {name} ({Convert.ToHexStringLower(hash)}) -> {name}.sig");
    return Verify(installer, versionText);
}

static int Verify(string installer, string versionText)
{
    if (!ClaspVersion.TryParse(versionText, out var version)) { Console.Error.WriteLine("Version must be X.Y.Z."); return 2; }
    var hash = SHA256.HashData(File.ReadAllBytes(installer));
    var signature = File.Exists(installer + ".sig") ? File.ReadAllText(installer + ".sig") : null;
    var valid = UpdateSignature.Verify(UpdateSignature.PublicKey, version, Path.GetFileName(installer), hash, signature);
    Console.WriteLine(valid ? "Signature verifies with the app's public key." : "Signature does NOT verify.");
    return valid ? 0 : 1;
}
