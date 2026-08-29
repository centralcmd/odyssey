using System.Security.Cryptography;
using System.Text;

namespace Odyssey.TestData;

/// <summary>
/// Produces stable <see cref="Guid"/>s from string keys. Using deterministic ids lets
/// the seeder build a coherent object graph (FKs wired without EF fixup), keeps E2E
/// references stable across runs, and makes idempotency checks trivial.
/// </summary>
public static class DeterministicGuid
{
    // Arbitrary fixed namespace so demo ids never collide with real, randomly-generated ones.
    private const string Namespace = "odyssey-demo-data::";

    public static Guid From(string key)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(Namespace + key));
        return new Guid(bytes);
    }

    public static Guid From(string prefix, int index) => From($"{prefix}#{index}");
}
